﻿using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyNetQ.Events;
using EasyNetQ.Producer;
using EasyNetQ.Topology;

namespace EasyNetQ.Rpc.ReuseQueue
{
    class ReuseQueueAdvancedClientRpc : IAdvancedClientRpc
    {
        private readonly IAdvancedBus _advancedBus;
        private readonly IConnectionConfiguration _configuration;
        private readonly IRpcHeaderKeys _rpcHeaderKeys;
        private readonly IAdvancedPublishExchangeDeclareStrategy _advancedPublishExchangeDeclareStrategy;
        private readonly string _responseQueueName;
        private readonly TimeSpan _disablePeriodicSignaling = TimeSpan.FromMilliseconds(-1);
        
        private readonly ConcurrentDictionary<string, ResponseAction> _responseActions = new ConcurrentDictionary<string, ResponseAction>();
        private IDisposable _consumer;

        public ReuseQueueAdvancedClientRpc(
            IAdvancedBus advancedBus, 
            IConnectionConfiguration configuration, 
            IRpcHeaderKeys rpcHeaderKeys, 
            IEventBus eventBus, 
            IAdvancedPublishExchangeDeclareStrategy advancedPublishExchangeDeclareStrategy, 
            string responseQueueName)
        {
            _advancedBus = advancedBus;
            _configuration = configuration;
            _rpcHeaderKeys = rpcHeaderKeys;
            _advancedPublishExchangeDeclareStrategy = advancedPublishExchangeDeclareStrategy;
            _responseQueueName = responseQueueName;
            eventBus.Subscribe<ConnectionCreatedEvent>(_ => OnConnectionCreated());
            eventBus.Subscribe<ConnectionDisconnectedEvent>(_ => OnConnectionDisconnected());
            CreateQueueAndConsume();
        }

        public Task<SerializedMessage> RequestAsync(IExchange requestExchange, string requestRoutingKey, bool mandatory, bool immediate, TimeSpan timeout, SerializedMessage request)
        {
            Preconditions.CheckNotNull(request, "request");
            Preconditions.CheckNotNull(requestExchange, "requestExchange");
            Preconditions.CheckNotNull(requestRoutingKey, "requestRoutingKey");

            var exchangeType = ExchangeType.Direct;
            var correlationId = Guid.NewGuid();

            var tcs = new TaskCompletionSource<SerializedMessage>();
            var timer = new Timer(state =>
            {
                ((Timer)state).Dispose();
                tcs.TrySetException(new TimeoutException(
                    string.Format("Request timed out. CorrelationId: {0}", correlationId.ToString())));
            });

            timer.Change(TimeSpan.FromSeconds(_configuration.Timeout), _disablePeriodicSignaling);

            RegisterMessageHandling(correlationId, timer, tcs);

            RequestPublish(request, requestRoutingKey, requestExchange, exchangeType, _responseQueueName, correlationId, timeout);

            return tcs.Task;
        }

        private void RequestPublish<TRequest>(TRequest request, string routingKey, IExchange exchange, string exchangeType, string returnQueueName, Guid correlationId, TimeSpan timeout)
            where TRequest : class
        {
            _advancedPublishExchangeDeclareStrategy.DeclareExchange(_advancedBus, exchange.Name, exchangeType);

            var requestMessage = new Message<TRequest>(request)
            {
                Properties =
                {
                    ReplyTo = returnQueueName,
                    CorrelationId = correlationId.ToString(),
                    Expiration = ((int)timeout.TotalMilliseconds).ToString()
                }
            };

            _advancedBus.Publish(exchange, routingKey, false, false, requestMessage);
        }

        private void OnConnectionDisconnected()
        {
            _consumer.Dispose();
        }

        private void OnConnectionCreated()
        {
            var copyOfResponseActions = _responseActions.Values;
            _responseActions.Clear();

            // retry in-flight requests.
            foreach (var responseAction in copyOfResponseActions)
            {
                responseAction.OnFailure();
            }

            CreateQueueAndConsume();
        }

        private void CreateQueueAndConsume()
        {
            //TODO The queue is exclusive, which gives it a transient consumer. (Wait what ??)
            //This is bad bad bad. It is relying on being a non-reconsuming (transient) consumer, due to the fact that it is exclusive. MUST BE FIXED asap
            var queue = _advancedBus.QueueDeclare(
                _responseQueueName,
                passive: false,
                durable: false,
                exclusive: true,
                autoDelete: true);

            _consumer = _advancedBus.Consume(queue, (bytes, properties, info) => Task.Factory.StartNew(() => 
                {
                    var msg = new SerializedMessage(properties, bytes);
                    ResponseAction responseAction;
                    if (_responseActions.TryRemove(msg.Properties.CorrelationId, out responseAction))
                    {
                        responseAction.OnSuccess(msg);
                    }
                }));
        }

        private void RegisterMessageHandling(Guid correlationId, Timer timer, TaskCompletionSource<SerializedMessage> tcs)
        {
            _responseActions.TryAdd(correlationId.ToString(), new ResponseAction
            {
                OnSuccess = smsg =>
                {
                    timer.Dispose();

                    var isFaulted = false;
                    var exceptionMessage = "The exception message has not been specified.";
                    if (smsg.Properties.HeadersPresent)
                    {
                        if (smsg.Properties.Headers.ContainsKey(_rpcHeaderKeys.IsFaultedKey))
                        {
                            isFaulted = Convert.ToBoolean(smsg.Properties.Headers[_rpcHeaderKeys.IsFaultedKey]);
                        }
                        if (smsg.Properties.Headers.ContainsKey(_rpcHeaderKeys.ExceptionMessageKey))
                        {
                            exceptionMessage = Encoding.UTF8.GetString((byte[])smsg.Properties.Headers[_rpcHeaderKeys.ExceptionMessageKey]);
                        }
                    }

                    if (isFaulted)
                    {
                        tcs.TrySetException(new EasyNetQResponderException(exceptionMessage));
                    }
                    else
                    {
                        tcs.TrySetResult(smsg);
                    }
                },
                OnFailure = () =>
                {
                    timer.Dispose();
                    tcs.TrySetException(new EasyNetQException(
                        "Connection lost while request was in-flight. CorrelationId: {0}", correlationId.ToString()));
                }
            });
        }


        private class ResponseAction
        {
            public Action<SerializedMessage> OnSuccess { get; set; }
            public Action OnFailure { get; set; }
        }
    }
}