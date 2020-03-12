// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using Microsoft;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.RpcContracts.OutputChannel;
using Microsoft.VisualStudio.Shell.ServiceBroker;
using Microsoft.VisualStudio.Threading;
using IAsyncServiceProvider = Microsoft.VisualStudio.Shell.IAsyncServiceProvider;
using Task = System.Threading.Tasks.Task;

namespace NuGetConsole
{
    internal class ChannelOutputConsole : BaseOutputConsole, IDisposable
    {
        private static readonly Encoding TextEncoding = Encoding.UTF8;

        private readonly List<string> _deferredOutputMessages = new List<string>();
        private readonly AsyncSemaphore _pipeLock = new AsyncSemaphore(1);

        private Guid _channelGuid;
        private readonly string _channelId;
        private readonly string _channelOutputName;
        private readonly JoinableTaskFactory _joinableTaskFactory;

        private AsyncLazy<ServiceBrokerClient> _serviceBrokerClient;
        private PipeWriter _channelPipeWriter;
        private bool _disposedValue = false;

        public ChannelOutputConsole(IAsyncServiceProvider asyncServiceProvider, Guid channelId, string outputName, JoinableTaskFactory joinableTaskFactory)
        {
            if (asyncServiceProvider == null)
            {
                throw new ArgumentNullException(nameof(asyncServiceProvider));
            }
            _channelGuid = channelId;
            _channelId = _channelGuid.ToString();
            _channelOutputName = outputName ?? throw new ArgumentNullException(nameof(outputName));
            _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));

            _serviceBrokerClient = new AsyncLazy<ServiceBrokerClient>(async () =>
            {
                IBrokeredServiceContainer container = (IBrokeredServiceContainer)await asyncServiceProvider.GetServiceAsync(typeof(SVsBrokeredServiceContainer));
                Assumes.Present(container);
                IServiceBroker sb = container.GetFullAccessServiceBroker();
                return new ServiceBrokerClient(sb, _joinableTaskFactory);
            }, _joinableTaskFactory);
        }

        public override Task ActivateAsync()
        {
            // Nothing to do here in Visual Studio Online Environments mode. We do not create the pane
            return Task.CompletedTask;
        }

        public override Task ClearAsync()
        {
            return CloseChannelAsync();
        }

        public override Task WriteAsync(string text)
        {
            return WriteToOutputChannelAsync(_channelId, _channelOutputName, text, CancellationToken.None);
        }

        private async Task WriteToOutputChannelAsync(string channelId, string displayNameResourceId, string content, CancellationToken cancellationToken)
        {
            using (await _pipeLock.EnterAsync())
            {
                if (_channelPipeWriter == null)
                {
                    var pipe = new Pipe();

                    using (var outputChannelStore = await (await _serviceBrokerClient.GetValueAsync()).GetProxyAsync<IOutputChannelStore>(VisualStudioServices.VS2019_4.OutputChannelStore, cancellationToken))
                    {
                        if (outputChannelStore.Proxy != null)
                        {
                            await outputChannelStore.Proxy.CreateChannelAsync(channelId, displayNameResourceId, pipe.Reader, TextEncoding, cancellationToken);
                            _channelPipeWriter = pipe.Writer;

                            // write any deferred messages
                            foreach (var s in _deferredOutputMessages)
                            {
                                // Flush when the original content is logged below
                                await _channelPipeWriter.WriteAsync(GetBytes(content), cancellationToken);
                            }
                            _deferredOutputMessages.Clear();
                        }
                        else
                        {
                            // OutputChannel is not available so cache the output messages for later
                            _deferredOutputMessages.Add(content);
                            return;
                        }
                    }
                }
                await _channelPipeWriter.WriteAsync(GetBytes(content), cancellationToken);
                await _channelPipeWriter.FlushAsync(cancellationToken);
            }
        }

        private static byte[] GetBytes(string content)
        {
            return TextEncoding.GetBytes(content);
        }

        private async Task CloseChannelAsync()
        {
            using (await _pipeLock.EnterAsync())
            {
                _channelPipeWriter?.CancelPendingFlush();
                _channelPipeWriter?.Complete();
                _channelPipeWriter = null;
            }
        }

        public override void StartConsoleDispatcher()
        {
            // Nothing to do here in Visual Studio Online Environments mode. We do not create the pane
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    if (_serviceBrokerClient.IsValueCreated)
                    {
                        _serviceBrokerClient.GetValue().Dispose();
                    }
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                    CloseChannelAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
