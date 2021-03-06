﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="MongoSubscriptionPersister.cs" company="SharkByte Software">
//   The MIT License (MIT)
//   
//   Copyright (c) 2015 SharkByte Software
//   
//   Permission is hereby granted, free of charge, to any person obtaining a copy of
//   this software and associated documentation files (the "Software"), to deal in
//   the Software without restriction, including without limitation the rights to
//   use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
//   the Software, and to permit persons to whom the Software is furnished to do so,
//   subject to the following conditions:
//   
//   The above copyright notice and this permission notice shall be included in all
//   copies or substantial portions of the Software.
//   
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
//   FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
//   COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
//   IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
//   CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// </copyright>
// <summary>
//   The MongoDB subscription persister.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace NServiceBus.MongoDB.SubscriptionPersister
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.Linq;

    using global::MongoDB.Driver;
    using global::MongoDB.Driver.Builders;

    using NServiceBus.MongoDB.Extensions;
    using NServiceBus.MongoDB.Internals;
    using NServiceBus.Unicast.Subscriptions;
    using NServiceBus.Unicast.Subscriptions.MessageDrivenSubscriptions;

    /// <summary>
    /// The MongoDB subscription persister.
    /// </summary>
    public sealed class MongoSubscriptionPersister : ISubscriptionStorage
    {
        private static readonly string SubscriptionName = typeof(Subscription).Name;

        private readonly MongoDatabase mongoDatabase;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoSubscriptionPersister"/> class. 
        /// Initializes a new instance of the <see cref="MongoSubscriptionStorage"/> class.
        /// </summary>
        /// <param name="mongoFactory">
        /// The MongoDB factory.
        /// </param>
        public MongoSubscriptionPersister(MongoDatabaseFactory mongoFactory)
        {
            Contract.Requires<ArgumentNullException>(mongoFactory != null);
            this.mongoDatabase = mongoFactory.GetDatabase();
        }

        /// <summary>
        /// The initialization method.
        /// </summary>
        public void Init()
        {
        }

        /// <summary>
        /// The subscribe.
        /// </summary>
        /// <param name="client">
        /// The client.
        /// </param>
        /// <param name="messageTypes">
        /// The message types.
        /// </param>
        void ISubscriptionStorage.Subscribe(Address client, IEnumerable<MessageType> messageTypes)
        {
            var messageTypeLookup = messageTypes.AssumedNotNull().ToDictionary(Subscription.FormatId);

            var existingSubscriptions = this.GetSubscriptions(messageTypeLookup.Values).ToDictionary(m => m.Id);
            var newSubscriptions = new List<Subscription>();

            //// TODO: section needs to be refactored/simplified
            messageTypeLookup.ToList().ForEach(
                mt =>
                    {
                        if (!existingSubscriptions.ContainsKey(mt.Key))
                        {
                            newSubscriptions.Add(new Subscription(mt.Value, new List<Address>() { client }));
                            return;
                        }

                        var existing = existingSubscriptions[mt.Key];

                        if (existing.Clients.All(c => c != client))
                        {
                            existing.Clients.Add(client);
                        }
                    });

            var collection = this.mongoDatabase.GetCollection(SubscriptionName).AssumedNotNull();
            existingSubscriptions.Values.ToList().ForEach(
                s =>
                    {
                        var query = s.MongoUpdateQuery();
                        var update = s.MongoUpdate();
                        var updateResult = collection.Update(query, update, UpdateFlags.None);
                        if (!updateResult.UpdatedExisting)
                        {
                            throw new InvalidOperationException(
                                string.Format("Unable to update subscription with id {0}", s.Id));
                        }
                    });
            
            if (!newSubscriptions.Any())
            {
                return;
            }

            var insertResult = collection.InsertBatch(newSubscriptions);
            if (insertResult.Any(r => r.HasLastErrorMessage))
            {
                throw new InvalidOperationException(string.Format("Unable to save {0} subscription", client));
            }
        }

        /// <summary>
        /// The unsubscribe.
        /// </summary>
        /// <param name="client">
        /// The client.
        /// </param>
        /// <param name="messageTypes">
        /// The message types.
        /// </param>
        void ISubscriptionStorage.Unsubscribe(Address client, IEnumerable<MessageType> messageTypes)
        {
            var queries =
                messageTypes.AssumedNotNull()
                            .Select(mt => Query<Subscription>.EQ(s => s.Id, Subscription.FormatId(mt)))
                            .ToList();
            var update = Update<Subscription>.Pull(subscription => subscription.Clients, client);

            var collection = this.mongoDatabase.GetCollection(SubscriptionName).AssumedNotNull();
            var results = queries.Select(q => collection.Update(q, update)).Where(result => result.HasLastErrorMessage);

            if (results.Any())
            {
                throw new InvalidOperationException(
                    string.Format("Unable to unsubscribe {0} from one of its subscriptions", client));
            }
        }

        /// <summary>
        /// The get subscriber addresses for message.
        /// </summary>
        /// <param name="messageTypes">
        /// The message types.
        /// </param>
        /// <returns>
        /// The <see cref="IEnumerable{T}"/>.
        /// </returns>
        IEnumerable<Address> ISubscriptionStorage.GetSubscriberAddressesForMessage(IEnumerable<MessageType> messageTypes)
        {
            var subscriptions = this.GetSubscriptions(messageTypes.AssumedNotNull());
            return subscriptions.SelectMany(s => s.Clients).Distinct().ToArray();
        }

        internal IEnumerable<Subscription> GetSubscriptions(IEnumerable<MessageType> messageTypes)
        {
            Contract.Requires(messageTypes != null);
            Contract.Ensures(Contract.Result<IEnumerable<Subscription>>() != null);

            var collection = this.mongoDatabase.GetCollection(SubscriptionName).AssumedNotNull();

            var ids = messageTypes.Select(Subscription.FormatId);
            var query = Query<Subscription>.In(p => p.Id, ids);
            var result = collection.FindAs<Subscription>(query);

            return result.AssumedNotNull();
        }

        [ContractInvariantMethod]
        private void ObjectInvariants()
        {
            Contract.Invariant(this.mongoDatabase != null);
        }
    }
}
