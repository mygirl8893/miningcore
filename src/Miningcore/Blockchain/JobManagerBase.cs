/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.IO;
using System.IO.Compression;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Util;
using NLog;
using ZeroMQ;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain
{
    public abstract class JobManagerBase<TJob>
    {
        protected JobManagerBase(IComponentContext ctx, IMessageBus messageBus)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(messageBus, nameof(messageBus));

            this.ctx = ctx;
            this.messageBus = messageBus;
        }

        protected readonly IComponentContext ctx;
        protected readonly IMessageBus messageBus;
        protected ClusterConfig clusterConfig;

        protected TJob currentJob;
        private int jobId;
        protected object jobLock = new object();
        protected ILogger logger;
        protected PoolConfig poolConfig;
        protected bool hasInitialBlockTemplate = false;
        protected Subject<Unit> blockSubmissionSubject = new Subject<Unit>();

        protected abstract void ConfigureDaemons();

        protected virtual async Task StartDaemonAsync(CancellationToken ct)
        {
            while(!await AreDaemonsHealthyAsync())
            {
                logger.Info(() => $"Waiting for daemons to come online ...");

                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }

            logger.Info(() => $"All daemons online");

            while(!await AreDaemonsConnectedAsync())
            {
                logger.Info(() => $"Waiting for daemons to connect to peers ...");

                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }

        protected string NextJobId(string format = null)
        {
            Interlocked.Increment(ref jobId);
            var value = Interlocked.CompareExchange(ref jobId, 0, Int32.MinValue);

            if (format != null)
                return value.ToString(format);

            return value.ToStringHex8();
        }

        protected IObservable<string> BtStreamSubscribe(ZmqPubSubEndpointConfig config)
        {
            return Observable.Defer(() => Observable.Create<string>(obs =>
            {
                var tcs = new CancellationTokenSource();

                Task.Factory.StartNew(() =>
                {
                    using(tcs)
                    {
                        while(!tcs.IsCancellationRequested)
                        {
                            try
                            {
                                using(var subSocket = new ZSocket(ZSocketType.SUB))
                                {
                                    //subSocket.Options.ReceiveHighWatermark = 1000;
                                    subSocket.SetupCurveTlsClient(config.SharedEncryptionKey, logger);
                                    subSocket.Connect(config.Url);
                                    subSocket.Subscribe(config.Topic);

                                    logger.Debug($"Subscribed to {config.Url}/{config.Topic}");

                                    while(!tcs.IsCancellationRequested)
                                    {
                                        // string topic;
                                        uint flags;
                                        byte[] data;
                                        // long timestamp;

                                        using (var msg = subSocket.ReceiveMessage())
                                        {
                                            // extract frames
                                            // topic = msg[0].ToString(Encoding.UTF8);
                                            flags = msg[1].ReadUInt32();
                                            data = msg[2].Read();
                                            // timestamp = msg[3].ReadInt64();
                                        }

                                        // TMP FIX
                                        if (flags != 0 && ((flags & 1) == 0))
                                            flags = BitConverter.ToUInt32(BitConverter.GetBytes(flags).ToNewReverseArray());

                                        // compressed
                                        if ((flags & 1) == 1)
                                        {
                                            using(var stm = new MemoryStream(data))
                                            {
                                                using(var stmOut = new MemoryStream())
                                                {
                                                    using(var ds = new DeflateStream(stm, CompressionMode.Decompress))
                                                    {
                                                        ds.CopyTo(stmOut);
                                                    }

                                                    data = stmOut.ToArray();
                                                }
                                            }
                                        }

                                        // convert
                                        var json = Encoding.UTF8.GetString(data);

                                        // publish
                                        obs.OnNext(json);

                                        // telemetry
                                        //messageBus.SendMessage(new TelemetryEvent(clusterConfig.ClusterName ?? poolConfig.PoolName, poolConfig.Id,
                                        //    TelemetryCategory.BtStream, DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp)));
                                    }
                                }
                            }

                            catch(Exception ex)
                            {
                                logger.Error(ex);
                            }

                            // do not consume all CPU cycles in case of a long lasting error condition
                            Thread.Sleep(1000);
                        }
                    }
                }, tcs.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                return Disposable.Create(() => { tcs.Cancel(); });
            }))
            .Publish()
            .RefCount();
        }

        protected abstract Task<bool> AreDaemonsHealthyAsync();
        protected abstract Task<bool> AreDaemonsConnectedAsync();
        protected abstract Task EnsureDaemonsSynchedAsync(CancellationToken ct);
        protected abstract Task PostStartInitAsync(CancellationToken ct);

        #region API-Surface

        public virtual void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(clusterConfig, nameof(clusterConfig));

            logger = LogUtil.GetPoolScopedLogger(typeof(JobManagerBase<TJob>), poolConfig);
            this.poolConfig = poolConfig;
            this.clusterConfig = clusterConfig;

            ConfigureDaemons();
        }

        public async Task StartAsync(CancellationToken ct)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));

            logger.Info(() => $"Starting Job Manager ...");

            await StartDaemonAsync(ct);
            await EnsureDaemonsSynchedAsync(ct);
            await PostStartInitAsync(ct);

            logger.Info(() => $"Job Manager Online");
        }

        #endregion // API-Surface
    }
}
