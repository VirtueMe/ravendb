using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Transactions;
using Newtonsoft.Json.Linq;
using Raven.Client.Client;
using Raven.Client.Exceptions;
using Raven.Database;
using Raven.Database.Data;

#if !NET_3_5
using System.Dynamic;
using Microsoft.CSharp.RuntimeBinder;
using Raven.Database.Linq;
#endif

namespace Raven.Client.Document
{
	/// <summary>
	/// Abstract implementation for in memory session operations
	/// </summary>
	public abstract class InMemoryDocumentSessionOperations : IDisposable
	{
		private const string RavenEntityName = "Raven-Entity-Name";
		/// <summary>
		/// The entities waiting to be deleted
		/// </summary>
		protected readonly HashSet<object> deletedEntities = new HashSet<object>();

		private bool hasEnlisted;

		/// <summary>
		/// hold the data required to manage the data for RavenDB's Unit of Work
		/// </summary>
		protected readonly Dictionary<object, DocumentSession.DocumentMetadata> entitiesAndMetadata =
			new Dictionary<object, DocumentSession.DocumentMetadata>();

		/// <summary>
		/// Translate between a key and its associated entity
		/// </summary>
        protected readonly Dictionary<string, object> entitiesByKey = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);
		/// <summary>
		/// The document store associated with this session
		/// </summary>
		protected DocumentStore documentStore;

		/// <summary>
		/// Gets the number of requests for this session
		/// </summary>
		/// <value></value>
		public int NumberOfRequests { get; private set; }

		private readonly IDocumentDeleteListener[] deleteListeners;
		private readonly IDocumentStoreListener[] storeListeners;

		/// <summary>
		/// Initializes a new instance of the <see cref="InMemoryDocumentSessionOperations"/> class.
		/// </summary>
		/// <param name="documentStore">The document store.</param>
		/// <param name="storeListeners">The store listeners.</param>
		/// <param name="deleteListeners">The delete listeners.</param>
		protected InMemoryDocumentSessionOperations(DocumentStore documentStore, IDocumentStoreListener[] storeListeners, IDocumentDeleteListener[] deleteListeners)
		{
			this.documentStore = documentStore;
			this.deleteListeners = deleteListeners;
			this.storeListeners = storeListeners;
		    ResourceManagerId = documentStore.ResourceManagerId;
            UseOptimisticConcurrency = false;
			AllowNonAuthoritiveInformation = true;
			NonAuthoritiveInformationTimeout = TimeSpan.FromSeconds(15);
		    MaxNumberOfRequestsPerSession = documentStore.Conventions.MaxNumberOfRequestsPerSession;
		}

		/// <summary>
		/// Gets or sets the timeout to wait for authoritive information if encountered non authoritive document.
		/// </summary>
		/// <value></value>
		public TimeSpan NonAuthoritiveInformationTimeout { get; set; }

		/// <summary>
		/// Gets the store identifier for this session.
		/// The store identifier is the identifier for the particular RavenDB instance.
		/// This is mostly useful when using sharding.
		/// </summary>
		/// <value>The store identifier.</value>
		public string StoreIdentifier
		{
			get { return documentStore.Identifier; }
		}

		/// <summary>
		/// Gets the conventions used by this session
		/// </summary>
		/// <value>The conventions.</value>
		/// <remarks>
		/// This instance is shared among all sessions, changes to the <see cref="DocumentConvention"/> should be done
		/// via the <see cref="IDocumentStore"/> instance, not on a single session.
		/// </remarks>
		public DocumentConvention Conventions
		{
			get { return documentStore.Conventions; }
		}

        /// <summary>
        /// The transaction resource manager identifer
        /// </summary>
        public Guid ResourceManagerId { get; private set; }


		/// <summary>
		/// Gets or sets the max number of requests per session.
		/// If the <see cref="NumberOfRequests"/> rise above <see cref="MaxNumberOfRequestsPerSession"/>, an exception will be thrown.
		/// </summary>
		/// <value>The max number of requests per session.</value>
		public int MaxNumberOfRequestsPerSession { get; set; }

		/// <summary>
		/// Gets or sets a value indicating whether the session should use optimistic concurrency.
		/// When set to <c>true</c>, a check is made so that a change made behind the session back would fail
		/// and raise <see cref="Raven.Http.Exceptions.ConcurrencyException"/>.
		/// </summary>
		/// <value></value>
		public bool UseOptimisticConcurrency { get; set; }
		/// <summary>
		/// Occurs when an entity is stored in the session
		/// </summary>
		public virtual event EntityStored Stored;
		/// <summary>
		/// Occurs when an entity is converted to a document and metadata.
		/// Changes made to the document / metadata instances passed to this event will be persisted.
		/// </summary>
		public virtual event EntityToDocument OnEntityConverted;

		/// <summary>
		/// Gets the metadata for the specified entity.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="instance">The instance.</param>
		/// <returns></returns>
		public JObject GetMetadataFor<T>(T instance)
		{
			DocumentSession.DocumentMetadata value;
			if (entitiesAndMetadata.TryGetValue(instance, out value) == false)
			{
			    string id;
			    if(TryGetIdFromInstance(instance, out id)
#if !NET_3_5
                    || (instance is IDynamicMetaObjectProvider && 
                    TryGetIdFromDynamic(instance, out id) )
#endif 
                    )
			    {
			        var jsonDocument = GetJsonDocument(id);
			        entitiesByKey[id] = instance;
                    entitiesAndMetadata[instance] = value = new DocumentSession.DocumentMetadata
                    {
                        ETag = UseOptimisticConcurrency ? (Guid?)Guid.Empty : null,
                        Key = id,
                        OriginalMetadata = jsonDocument.Metadata,
                        Metadata = new JObject(jsonDocument.Metadata),
                        OriginalValue = new JObject()
                    };
			    }
                else
			    {
			        throw new InvalidOperationException("Could not find the document key for " + instance);
			    }
			}
			return value.Metadata;
		}

        /// <summary>
        /// Get the json document by key from the store
        /// </summary>
	    protected abstract JsonDocument GetJsonDocument(string documentKey);

		/// <summary>
		/// Gets the document id.
		/// </summary>
		/// <param name="instance">The instance.</param>
		/// <returns></returns>
		public string GetDocumentId(object instance)
		{
			DocumentSession.DocumentMetadata value;
			if (entitiesAndMetadata.TryGetValue(instance, out value) == false)
				return null;
			return value.Key;
		}
		/// <summary>
		/// Gets a value indicating whether any of the entities tracked by the session has changes.
		/// </summary>
		/// <value></value>
		public bool HasChanges
		{
			get 
			{
				return deletedEntities.Count > 0 ||
						entitiesAndMetadata.Where(pair => EntityChanged(pair.Key, pair.Value)).Any();
			}
		}


		/// <summary>
		/// Determines whether the specified entity has changed.
		/// </summary>
		/// <param name="entity">The entity.</param>
		/// <returns>
		/// 	<c>true</c> if the specified entity has changed; otherwise, <c>false</c>.
		/// </returns>
		public bool HasChanged(object entity)
		{
			DocumentSession.DocumentMetadata value;
			if (entitiesAndMetadata.TryGetValue(entity, out value) == false)
				return false;
			return EntityChanged(entity, value);
		}

        internal void DecrementRequestCount()
        {
            --NumberOfRequests;
        }

	    internal void IncrementRequestCount()
		{
			if (++NumberOfRequests > MaxNumberOfRequestsPerSession)
				throw new InvalidOperationException(
					string.Format(
						@"The maximum number of requests ({0}) allowed for this session has been reached.
Raven limits the number of remote calls that a session is allowed to make as an early warning system. Sessions are expected to be short lived, and 
Raven provides facilities like Load(string[] keys) to load multiple documents at once and batch saves.
You can increase the limit by setting DocumentConvention.MaxNumberOfRequestsPerSession or DocumentSession.MaxNumberOfRequestsPerSession, but it is
advisable that you'll look into reducing the number of remote calls first, since that will speed up your application signficantly and result in a 
more responsive application.
",
						MaxNumberOfRequestsPerSession));
		}

		/// <summary>
		/// Tracks the entity inside the unit of work
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="documentFound">The document found.</param>
		/// <returns></returns>
		protected T TrackEntity<T>(JsonDocument documentFound)
		{
			if (documentFound.Metadata.Property("@etag") == null)
			{
				documentFound.Metadata.Add("@etag", new JValue(documentFound.Etag.ToString()));
			}
			if(documentFound.NonAuthoritiveInformation && AllowNonAuthoritiveInformation == false)
			{
				throw new NonAuthoritiveInformationException("Document " + documentFound.Key +
				" returned Non Authoritive Information (probably modified by a transaction in progress) and AllowNonAuthoritiveInformation  is set to false");
			}
			return TrackEntity<T>(documentFound.Key, documentFound.DataAsJson, documentFound.Metadata);
		}

		/// <summary>
		/// Tracks the entity.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="key">The key.</param>
		/// <param name="document">The document.</param>
		/// <param name="metadata">The metadata.</param>
		/// <returns></returns>
		public T TrackEntity<T>(string key, JObject document, JObject metadata)
		{
			object entity;
			if (entitiesByKey.TryGetValue(key, out entity) == false)
			{
				entity = ConvertToEntity<T>(key, document, metadata);
			}
			else
			{
				// the local instnace may have been changed, we adhere to the current Unit of Work
				// instance, and return that, ignoring anything new.
				return (T) entity;
			}
			var etag = metadata.Value<string>("@etag");
			document.Remove("@metadata");
			if(metadata.Value<bool>("Non-Authoritive-Information") && 
				AllowNonAuthoritiveInformation == false)
			{
				throw new NonAuthoritiveInformationException("Document " + key +
					" returned Non Authoritive Information (probably modified by a transaction in progress) and AllowNonAuthoritiveInformation  is set to false");
			}
			entitiesAndMetadata[entity] = new DocumentSession.DocumentMetadata
			{
				OriginalValue = document,
				Metadata = metadata,
				OriginalMetadata = new JObject(metadata),
				ETag = new Guid(etag),
				Key = key
			};
			entitiesByKey[key] = entity;
			return (T) entity;
		}

		/// <summary>
		/// Gets or sets a value indicating whether non authoritive information is allowed.
		/// Non authoritive information is document that has been modified by a transaction that hasn't been committed.
		/// The server provides the latest committed version, but it is known that attempting to write to a non authoritive document
		/// will fail, because it is already modified.
		/// If set to <c>false</c>, the session will wait <see cref="NonAuthoritiveInformationTimeout"/> for the transaction to commit to get an
		/// authoritive information. If the wait is longer than <see cref="NonAuthoritiveInformationTimeout"/>, <see cref="NonAuthoritiveInformationException"/> is thrown.
		/// </summary>
		/// <value>
		/// 	<c>true</c> if non authoritive information is allowed; otherwise, <c>false</c>.
		/// </value>
		public bool AllowNonAuthoritiveInformation { get; set; }

		/// <summary>
		/// Marks the specified entity for deletion. The entity will be deleted when <see cref="IDocumentSession.SaveChanges"/> is called.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="entity">The entity.</param>
		public void Delete<T>(T entity)
		{
			if(entitiesAndMetadata.ContainsKey(entity)==false)
				throw new InvalidOperationException(entity+" is not associated with the session, cannot delete unknown entity instance");
			deletedEntities.Add(entity);
		}

		/// <summary>
		/// Converts the json document to an entity.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="id">The id.</param>
		/// <param name="documentFound">The document found.</param>
		/// <param name="metadata">The metadata.</param>
		/// <returns></returns>
		protected object ConvertToEntity<T>(string id, JObject documentFound, JObject metadata)
		{
			var entity = default(T);
			EnsureNotReadVetoed(metadata);
			var documentType = metadata.Value<string>("Raven-Clr-Type");
			if (documentType != null)
			{
				var type = Type.GetType(documentType);
				if (type != null)
					entity = (T) documentFound.Deserialize(type, Conventions);
			}
			if (Equals(entity, default(T)))
			{
				entity = documentFound.Deserialize<T>(Conventions);
#if !NET_3_5
				var document = entity as JObject;
				if (document != null)
				{
					entity = (T)(object)(new DynamicJsonObject(document));
				}
#endif
			}
			TrySetIdentity(entity, id);
			return entity;
		}

		/// <summary>
		/// Tries to set the identity property
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="entity">The entity.</param>
		/// <param name="id">The id.</param>
		protected internal void TrySetIdentity<T>(T entity, string id)
		{
			var identityProperty = documentStore.Conventions.GetIdentityProperty(entity.GetType());
			if (identityProperty != null && identityProperty.CanWrite)
			{
                if (identityProperty.PropertyType == typeof(string))
                {
                    identityProperty.SetValue(entity, id, null);
                }
                else // need converting
                {
                    var converter = Conventions.IdentityTypeConvertors.FirstOrDefault(x=>x.CanConvertFrom(identityProperty.PropertyType));
                    if(converter == null)
                        throw new ArgumentException("Could not convert identity to type " + identityProperty.PropertyType + " because there is not matching type converter registered in the conventions' IdentityTypeConvertors");

                    identityProperty.SetValue(entity, converter.ConvertTo(id), null);
                }
			}
		}

		private static void EnsureNotReadVetoed(JObject metadata)
		{
			var readVetoAsString = metadata.Value<string>("Raven-Read-Veto");
			if (readVetoAsString == null)
				return;

			var readVeto = JObject.Parse(readVetoAsString);

			var s = readVeto.Value<string>("Reason");
			throw new ReadVetoException(
				"Document could not be read because of a read veto."+Environment.NewLine +
				"The read was vetoed by: " + readVeto.Value<string>("Trigger") + Environment.NewLine + 
				"Veto reason: " + s
				);
		}

		/// <summary>
		/// Stores the specified entity in the session. The entity will be saved when <see cref="IDocumentSession.SaveChanges"/> is called.
		/// </summary>
		/// <param name="entity">The entity.</param>
		public void Store(object entity)
		{
			if (null == entity)
				throw new ArgumentNullException("entity");
			
			string id = null;
#if !NET_3_5
            if (entity is IDynamicMetaObjectProvider)
            {
            	if(TryGetIdFromDynamic(entity,out id) == false)
				{
					id = Conventions.DocumentKeyGenerator(entity);

					if (id != null)
					{
						// Store it back into the Id field so the client has access to to it                    
						((dynamic) entity).Id = id;
					}
				}
            }
            else
#endif
			{
				id = GetOrGenerateDocumentKey(entity);

				TrySetIdentity(entity, id);
			}

			// we make the check here even if we just generated the key
			// users can override the key generation behavior, and we need
			// to detect if they generate duplicates.
			if (id != null &&
				id.EndsWith("/") == false // not a prefix id
					&& entitiesByKey.ContainsKey(id))
			{
				if (ReferenceEquals(entitiesByKey[id], entity))
					return; // calling Store twice on the same reference is a no-op
				throw new NonUniqueObjectException("Attempted to associated a different object with id '" + id + "'.");
			}

			var tag = documentStore.Conventions.GetTypeTagName(entity.GetType());
			entitiesAndMetadata.Add(entity, new DocumentSession.DocumentMetadata
			{
				Key = id,
				Metadata = new JObject(new JProperty(RavenEntityName, new JValue(tag))),
				OriginalMetadata = new JObject(),
				ETag = UseOptimisticConcurrency ? (Guid?)Guid.Empty : null,
				OriginalValue = new JObject()
			});
			if (id != null)
				entitiesByKey[id] = entity;
		}

		/// <summary>
		/// Tries to get the identity.
		/// </summary>
		/// <param name="entity">The entity.</param>
		/// <returns></returns>
		protected string GetOrGenerateDocumentKey(object entity)
		{
		    string id;
		    TryGetIdFromInstance(entity, out id);

			if (id == null)
			{
				// Generate the key up front
				id = Conventions.GenerateDocumentKey(entity);

			}
			return id;
		}

	    private bool TryGetIdFromInstance(object entity, out string id)
        {
            var identityProperty = GetIdentityProperty(entity.GetType());
			if (identityProperty != null)
			{
			    var value = identityProperty.GetValue(entity, null);
			    id = value as string;
			    if(id == null && value != null) // need convertion
			    {
                    var converter = Conventions.IdentityTypeConvertors.FirstOrDefault(x => x.CanConvertFrom(value.GetType()));
                    if(converter == null)
                        throw new ArgumentException("Cannot use type " + value.GetType() + " as an identity without having a type converter registered for it in the conventions' IdentityTypeConvertors");
			        id = converter.ConvertFrom(value);
			    }
			    return true;
			}
            id = null;
            return false;
        }

#if !NET_3_5
		private static bool TryGetIdFromDynamic(dynamic entity, out string id)
		{
			try
			{
				id = entity.Id;
				return true;
			}
			catch (RuntimeBinderException)
			{
				id = null;
				return false;
			}
		}
#endif

		/// <summary>
		/// Creates the put entity command.
		/// </summary>
		/// <param name="entity">The entity.</param>
		/// <param name="documentMetadata">The document metadata.</param>
		/// <returns></returns>
		protected ICommandData CreatePutEntityCommand(object entity, DocumentSession.DocumentMetadata documentMetadata)
		{
			var json = ConvertEntityToJson(entity, documentMetadata.Metadata);

			var etag = UseOptimisticConcurrency ? documentMetadata.ETag : null;

			return new PutCommandData
			{
				Document = json,
				Etag = etag,
				Key = documentMetadata.Key,
				Metadata = documentMetadata.Metadata,
			};
		}

		private PropertyInfo GetIdentityProperty(Type entityType)
		{
			return documentStore.Conventions.GetIdentityProperty(entityType);
		}

		/// <summary>
		/// Updates the batch results.
		/// </summary>
		/// <param name="batchResults">The batch results.</param>
		/// <param name="entities">The entities.</param>
		protected void UpdateBatchResults(IList<BatchResult> batchResults, IList<object> entities)
		{
			var stored = Stored;
			for (var i = 0; i < batchResults.Count; i++)
			{
				var batchResult = batchResults[i];
				if (batchResult.Method != "PUT")
					continue;

				var entity = entities[i];
				DocumentSession.DocumentMetadata documentMetadata;
				if (entitiesAndMetadata.TryGetValue(entity, out documentMetadata) == false)
					continue;

				batchResult.Metadata["@etag"] = new JValue(batchResult.Etag.ToString());
				entitiesByKey[batchResult.Key] = entity;
				documentMetadata.ETag = batchResult.Etag;
				documentMetadata.Key = batchResult.Key;
				documentMetadata.OriginalMetadata = new JObject(batchResult.Metadata);
				documentMetadata.Metadata = batchResult.Metadata;
				documentMetadata.OriginalValue = ConvertEntityToJson(entity, documentMetadata.Metadata);

                TrySetIdentity(entity, batchResult.Key);

				if (stored != null)
					stored(entity);

				foreach (var documentStoreListener in storeListeners)
				{
					documentStoreListener.AfterStore(batchResult.Key, entity, batchResult.Metadata);
				}
            }
		}

		/// <summary>
		/// Prepares for save changes.
		/// </summary>
		/// <returns></returns>
		protected DocumentSession.SaveChangesData PrepareForSaveChanges()
		{
			var result = new DocumentSession.SaveChangesData
			{
				Entities = new List<object>(),
				Commands = new List<ICommandData>()
			};
			TryEnlistInAmbientTransaction();
			DocumentSession.DocumentMetadata value = null;
			foreach (var key in (from deletedEntity in deletedEntities
								 where entitiesAndMetadata.TryGetValue(deletedEntity, out value)
								 select value.Key))
			{
				Guid? etag = null;
				object existingEntity;
				DocumentSession.DocumentMetadata metadata = null;
				if (entitiesByKey.TryGetValue(key, out existingEntity))
				{
					if (entitiesAndMetadata.TryGetValue(existingEntity, out metadata))
						etag = metadata.ETag;
					entitiesAndMetadata.Remove(existingEntity);
					entitiesByKey.Remove(key);
				}

				etag = UseOptimisticConcurrency ? etag : null;
				result.Entities.Add(existingEntity);

				foreach (var deleteListener in deleteListeners)
				{
					deleteListener.BeforeDelete(key, existingEntity, metadata != null ? metadata.Metadata : null);
				}

				result.Commands.Add(new DeleteCommandData
				{
					Etag = etag,
					Key = key,
				});
			}
			deletedEntities.Clear();
			foreach (var entity in entitiesAndMetadata.Where(pair => EntityChanged(pair.Key, pair.Value)))
			{
				foreach (var documentStoreListener in storeListeners)
				{
					documentStoreListener.BeforeStore(entity.Value.Key, entity.Key, entity.Value.Metadata);
				}
				result.Entities.Add(entity.Key);
				if (entity.Value.Key != null)
					entitiesByKey.Remove(entity.Value.Key);
				result.Commands.Add(CreatePutEntityCommand(entity.Key, entity.Value));
			}

			return result;
		}

		private void TryEnlistInAmbientTransaction()
		{
			if (hasEnlisted || Transaction.Current == null) 
				return;


			var transactionalSession = (ITransactionalDocumentSession)this;
			if (documentStore.DatabaseCommands.SupportsPromotableTransactions == false ||
				Transaction.Current.EnlistPromotableSinglePhase(new PromotableRavenClientEnlistment(transactionalSession)) == false) 
			{
				Transaction.Current.EnlistDurable(
					ResourceManagerId, 
					new RavenClientEnlistment(transactionalSession),
					EnlistmentOptions.None);
			}
			hasEnlisted = true;
		}

		/// <summary>
		/// Determines if the entity have changed.
		/// </summary>
		/// <param name="entity">The entity.</param>
		/// <param name="documentMetadata">The document metadata.</param>
		/// <returns></returns>
		protected bool EntityChanged(object entity, DocumentSession.DocumentMetadata documentMetadata)
		{
			if (documentMetadata == null)
				return true; 
			var newObj = ConvertEntityToJson(entity, documentMetadata.Metadata);
			var equalityComparer = new JTokenEqualityComparer();
			return equalityComparer.Equals(newObj, documentMetadata.OriginalValue) == false ||
				equalityComparer.Equals(documentMetadata.Metadata, documentMetadata.OriginalMetadata) == false;
		}

		private JObject ConvertEntityToJson(object entity, JObject metadata)
		{
			var entityType = entity.GetType();
			var identityProperty = documentStore.Conventions.GetIdentityProperty(entityType);

			var objectAsJson = GetObjectAsJson(entity);
			if (identityProperty != null)
			{
				objectAsJson.Remove(identityProperty.Name);
			}

			metadata["Raven-Clr-Type"] = JToken.FromObject(ReflectionUtil.GetFullNameWithoutVersionInformation(entityType));

			var entityConverted = OnEntityConverted;
			if (entityConverted != null)
				entityConverted(entity, objectAsJson, metadata);

			return objectAsJson;
		}

		private JObject GetObjectAsJson(object entity)
		{
			return JObject.FromObject(entity, Conventions.CreateSerializer());
		}



		/// <summary>
		/// Evicts the specified entity from the session.
		/// Remove the entity from the delete queue and stops tracking changes for this entity.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="entity">The entity.</param>
		public void Evict<T>(T entity)
		{
			DocumentSession.DocumentMetadata value;
			if (entitiesAndMetadata.TryGetValue(entity, out value))
			{
				entitiesAndMetadata.Remove(entity);
				entitiesByKey.Remove(value.Key);
			}
			deletedEntities.Remove(entity);
		}

		/// <summary>
		/// Clears this instance.
		/// Remove all entities from the delete queue and stops tracking changes for all entities.
		/// </summary>
		public void Clear()
		{
			entitiesAndMetadata.Clear();
			deletedEntities.Clear();
			entitiesByKey.Clear();
		}

		/// <summary>
		/// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
		/// </summary>
		public virtual void Dispose()
		{
			
		}

		/// <summary>
		/// Commits the specified tx id.
		/// </summary>
		/// <param name="txId">The tx id.</param>
		public abstract void Commit(Guid txId);
		/// <summary>
		/// Rollbacks the specified tx id.
		/// </summary>
		/// <param name="txId">The tx id.</param>
		public abstract void Rollback(Guid txId);
		/// <summary>
		/// Promotes the transaction.
		/// </summary>
		/// <param name="fromTxId">From tx id.</param>
		/// <returns></returns>
		public abstract byte[] PromoteTransaction(Guid fromTxId);

		/// <summary>
		/// Clears the enlistment.
		/// </summary>
		protected void ClearEnlistment()
		{
			hasEnlisted = false;
		}
	}
}
