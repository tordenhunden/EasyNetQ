﻿using System;
using System.Threading.Tasks;
using EasyNetQ.Consumer;
using EasyNetQ.Topology;

namespace EasyNetQ.Rpc.FreshQueue
{
    class FreshQueueClientRpc : IAdvancedClientRpc
    {
        private readonly IAdvancedBus _advancedBus;
        private readonly IConnectionConfiguration _configuration;
        private readonly IRpcHeaderKeys _rpcHeaderKeys;
        
        public FreshQueueClientRpc(IAdvancedBus advancedBus, IConnectionConfiguration configuration, IRpcHeaderKeys rpcHeaderKeys)
        {
            _advancedBus = advancedBus;
            _configuration = configuration;
            _rpcHeaderKeys = rpcHeaderKeys;
        }
        public Task<SerializedMessage> RequestAsync(IExchange requestExchange, string requestRoutingKey, bool mandatory, bool immediate, TimeSpan timeout, SerializedMessage request)
        {
            Preconditions.CheckNotNull(requestExchange, "requestExchange");
            Preconditions.CheckNotNull(requestRoutingKey, "requestRoutingKey");
            Preconditions.CheckNotNull(request, "request");

            var correlationId = Guid.NewGuid();
            var responseQueueName = "rpc:"+correlationId;

            var queue = _advancedBus.QueueDeclare(
                responseQueueName,
                passive: false,
                durable: false,
                expires: (int) TimeSpan.FromSeconds(_configuration.Timeout).TotalMilliseconds,
                exclusive: true,
                autoDelete: true);

            //the response is published to the default exchange with the queue name as routingkey. So no need to bind to exchange
            var continuation = _advancedBus.ConsumeSingle(new Queue(queue.Name, queue.IsExclusive), timeout);
            
            PublishRequest(requestExchange, request, requestRoutingKey, responseQueueName, correlationId);
            return continuation
                .Then(mcc => TaskHelpers.FromResult(new SerializedMessage(mcc.Properties, mcc.Message)))
                .Then(sm => RpcHelpers.ExtractExceptionFromHeadersAndPropagateToTask(_rpcHeaderKeys, sm));
        }

        private void PublishRequest(IExchange requestExchange, SerializedMessage request, string requestRoutingKey, string responseQueueName, Guid correlationId)
        {
            request.Properties.ReplyTo = responseQueueName;
            request.Properties.CorrelationId = correlationId.ToString();
            request.Properties.Expiration = TimeSpan.FromSeconds(_configuration.Timeout).TotalMilliseconds.ToString();

            //TODO write a specific RPC publisher that handles BasicReturn. Then we can set immediate+mandatory to true and react accordingly (now it will time out)
            _advancedBus.Publish(requestExchange, requestRoutingKey, false, false, request.Properties, request.Body);
        }
    }
}