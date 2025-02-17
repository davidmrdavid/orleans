using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Persistence.AzureStorage;
using Orleans.Providers.Azure;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage for writing grain state data to Azure table storage.
    /// </summary>
    public class AzureTableGrainStorage : IGrainStorage, IRestExceptionDecoder, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly AzureTableStorageOptions options;
        private readonly ClusterOptions clusterOptions;
        private readonly SerializationManager serializationManager;
        private readonly IGrainFactory grainFactory;
        private readonly ITypeResolver typeResolver;
        private readonly ILogger logger;

        private GrainStateTableDataManager tableDataManager;
        private JsonSerializerSettings JsonSettings;

        // each property can hold 64KB of data and each entity can take 1MB in total, so 15 full properties take
        // 15 * 64 = 960 KB leaving room for the primary key, timestamp etc
        private const int MAX_DATA_CHUNK_SIZE = 64 * 1024;
        private const int MAX_STRING_PROPERTY_LENGTH = 32 * 1024;
        private const int MAX_DATA_CHUNKS_COUNT = 15;

        private const string BINARY_DATA_PROPERTY_NAME = "Data";
        private const string STRING_DATA_PROPERTY_NAME = "StringData";
        private string name;

        /// <summary> Default constructor </summary>
        public AzureTableGrainStorage(
            string name,
            AzureTableStorageOptions options,
            IOptions<ClusterOptions> clusterOptions,
            SerializationManager serializationManager,
            IGrainFactory grainFactory,
            ITypeResolver typeResolver,
            ILogger<AzureTableGrainStorage> logger)
        {
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.name = name;
            this.serializationManager = serializationManager;
            this.grainFactory = grainFactory;
            this.typeResolver = typeResolver;
            this.logger = logger;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainReference);
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_ReadingData, "Reading: GrainType={0} Pk={1} Grainid={2} from Table={3}", grainType, pk, grainReference, this.options.TableName);
            string partitionKey = pk;
            string rowKey = AzureTableUtils.SanitizeTableProperty(grainType);
            var entity = await tableDataManager.Read(partitionKey, rowKey).ConfigureAwait(false);
            if (entity is not null)
            {
                var loadedState = ConvertFromStorageFormat(entity, grainState.Type);
                grainState.RecordExists = loadedState != null;
                grainState.State = loadedState ?? Activator.CreateInstance(grainState.Type);
                grainState.ETag = entity.ETag.ToString();
            }

            // Else leave grainState in previous default condition
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainReference);
            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_WritingData, "Writing: GrainType={0} Pk={1} Grainid={2} ETag={3} to Table={4}", grainType, pk, grainReference, grainState.ETag, this.options.TableName);

            var rowKey = AzureTableUtils.SanitizeTableProperty(grainType);
            var entity = new TableEntity(pk, rowKey)
            {
                ETag = new ETag(grainState.ETag)
            };
            ConvertToStorageFormat(grainState.State, entity);
            try
            {
                await DoOptimisticUpdate(() => tableDataManager.Write(entity), grainType, grainReference, this.options.TableName, grainState.ETag).ConfigureAwait(false);
                grainState.ETag = entity.ETag.ToString();
                grainState.RecordExists = true;
            }
            catch (Exception exc)
            {
                logger.Error((int)AzureProviderErrorCode.AzureTableProvider_WriteError,
                    $"Error Writing: GrainType={grainType} Grainid={grainReference} ETag={grainState.ETag} to Table={this.options.TableName} Exception={exc.Message}", exc);
                throw;
            }
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <remarks>
        /// If the <c>DeleteStateOnClear</c> is set to <c>true</c> then the table row
        /// for this grain will be deleted / removed, otherwise the table row will be
        /// cleared by overwriting with default / null values.
        /// </remarks>
        /// <see cref="IGrainStorage.ClearStateAsync"/>
        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainReference);
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_WritingData, "Clearing: GrainType={0} Pk={1} Grainid={2} ETag={3} DeleteStateOnClear={4} from Table={5}", grainType, pk, grainReference, grainState.ETag, this.options.DeleteStateOnClear, this.options.TableName);
            var rowKey = AzureTableUtils.SanitizeTableProperty(grainType);
            var entity = new TableEntity(pk, rowKey)
            {
                ETag = new ETag(grainState.ETag)
            };
            string operation = "Clearing";
            try
            {
                if (this.options.DeleteStateOnClear)
                {
                    operation = "Deleting";
                    await DoOptimisticUpdate(() => tableDataManager.Delete(entity), grainType, grainReference, this.options.TableName, grainState.ETag).ConfigureAwait(false);
                }
                else
                {
                    await DoOptimisticUpdate(() => tableDataManager.Write(entity), grainType, grainReference, this.options.TableName, grainState.ETag).ConfigureAwait(false);
                }

                grainState.ETag = entity.ETag.ToString(); // Update in-memory data to the new ETag
                grainState.RecordExists = false;
            }
            catch (Exception exc)
            {
                logger.Error((int)AzureProviderErrorCode.AzureTableProvider_DeleteError, string.Format("Error {0}: GrainType={1} Grainid={2} ETag={3} from Table={4} Exception={5}",
                    operation, grainType, grainReference, grainState.ETag, this.options.TableName, exc.Message), exc);
                throw;
            }
        }

        private static async Task DoOptimisticUpdate(Func<Task> updateOperation, string grainType, GrainReference grainReference, string tableName, string currentETag)
        {
            try
            {
                await updateOperation.Invoke().ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.IsPreconditionFailed() || ex.IsConflict() || ex.IsNotFound())
            {
                throw new TableStorageUpdateConditionNotSatisfiedException(grainType, grainReference, tableName, "Unknown", currentETag, ex);
            }
        }

        /// <summary>
        /// Serialize to Azure storage format in either binary or JSON format.
        /// </summary>
        /// <param name="grainState">The grain state data to be serialized</param>
        /// <param name="entity">The Azure table entity the data should be stored in</param>
        /// <remarks>
        /// See:
        /// http://msdn.microsoft.com/en-us/library/system.web.script.serialization.javascriptserializer.aspx
        /// for more on the JSON serializer.
        /// </remarks>
        internal void ConvertToStorageFormat(object grainState, TableEntity entity)
        {
            int dataSize;
            IEnumerable<object> properties;
            string basePropertyName;

            if (this.options.UseJson)
            {
                // http://james.newtonking.com/json/help/index.html?topic=html/T_Newtonsoft_Json_JsonConvert.htm
                string data = Newtonsoft.Json.JsonConvert.SerializeObject(grainState, this.JsonSettings);

                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Writing JSON data size = {0} for grain id = Partition={1} / Row={2}",
                    data.Length, entity.PartitionKey, entity.RowKey);

                // each Unicode character takes 2 bytes
                dataSize = data.Length * 2;

                properties = SplitStringData(data).Select(t => (object)t);
                basePropertyName = STRING_DATA_PROPERTY_NAME;
            }
            else
            {
                // Convert to binary format

                byte[] data = this.serializationManager.SerializeToByteArray(grainState);

                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Writing binary data size = {0} for grain id = Partition={1} / Row={2}",
                    data.Length, entity.PartitionKey, entity.RowKey);

                dataSize = data.Length;

                properties = SplitBinaryData(data).Select(t => (object)t);
                basePropertyName = BINARY_DATA_PROPERTY_NAME;
            }

            CheckMaxDataSize(dataSize, MAX_DATA_CHUNK_SIZE * MAX_DATA_CHUNKS_COUNT);

            foreach (var keyValuePair in properties.Zip(GetPropertyNames(basePropertyName),
                (property, name) => new KeyValuePair<string, object>(name, property)))
            {
                entity[keyValuePair.Key] = keyValuePair.Value;
            }
        }

        private void CheckMaxDataSize(int dataSize, int maxDataSize)
        {
            if (dataSize > maxDataSize)
            {
                var msg = string.Format("Data too large to write to Azure table. Size={0} MaxSize={1}", dataSize, maxDataSize);
                logger.Error(0, msg);
                throw new ArgumentOutOfRangeException("GrainState.Size", msg);
            }
        }

        private static IEnumerable<string> SplitStringData(string stringData)
        {
            var startIndex = 0;
            while (startIndex < stringData.Length)
            {
                var chunkSize = Math.Min(MAX_STRING_PROPERTY_LENGTH, stringData.Length - startIndex);

                yield return stringData.Substring(startIndex, chunkSize);

                startIndex += chunkSize;
            }
        }

        private static IEnumerable<byte[]> SplitBinaryData(byte[] binaryData)
        {
            var startIndex = 0;
            while (startIndex < binaryData.Length)
            {
                var chunkSize = Math.Min(MAX_DATA_CHUNK_SIZE, binaryData.Length - startIndex);

                var chunk = new byte[chunkSize];
                Array.Copy(binaryData, startIndex, chunk, 0, chunkSize);
                yield return chunk;

                startIndex += chunkSize;
            }
        }

        private static IEnumerable<string> GetPropertyNames(string basePropertyName)
        {
            yield return basePropertyName;
            for (var i = 1; i < MAX_DATA_CHUNKS_COUNT; ++i)
            {
                yield return basePropertyName + i;
            }
        }

        private static IEnumerable<byte[]> ReadBinaryDataChunks(TableEntity entity)
        {
            foreach (var binaryDataPropertyName in GetPropertyNames(BINARY_DATA_PROPERTY_NAME))
            {
                if (entity.TryGetValue(binaryDataPropertyName, out var dataProperty))
                {
                    switch (dataProperty)
                    {
                        // if TablePayloadFormat.JsonNoMetadata is used
                        case string stringValue:
                            if (!string.IsNullOrEmpty(stringValue))
                            {
                                yield return Convert.FromBase64String(stringValue);
                            }
                            break;

                        // if any payload type providing metadata is used
                        case byte[] binaryValue:
                            if (binaryValue != null && binaryValue.Length > 0)
                            {
                                yield return binaryValue;
                            }
                            break;
                    }
                }
            }
        }

        private static byte[] ReadBinaryData(TableEntity entity)
        {
            var dataChunks = ReadBinaryDataChunks(entity).ToArray();
            var dataSize = dataChunks.Select(d => d.Length).Sum();
            var result = new byte[dataSize];
            var startIndex = 0;
            foreach (var dataChunk in dataChunks)
            {
                Array.Copy(dataChunk, 0, result, startIndex, dataChunk.Length);
                startIndex += dataChunk.Length;
            }
            return result;
        }

        private static IEnumerable<string> ReadStringDataChunks(TableEntity entity)
        {
            foreach (var stringDataPropertyName in GetPropertyNames(STRING_DATA_PROPERTY_NAME))
            {
                if (entity.TryGetValue(stringDataPropertyName, out var dataProperty))
                {
                    if (dataProperty is string {Length: > 0 } data)
                    {
                        yield return data;
                    }
                }
            }
        }

        private static string ReadStringData(TableEntity entity)
        {
            return string.Join(string.Empty, ReadStringDataChunks(entity));
        }

        /// <summary>
        /// Deserialize from Azure storage format
        /// </summary>
        /// <param name="entity">The Azure table entity the stored data</param>
        /// <param name="stateType">The state type</param>
        internal object ConvertFromStorageFormat(TableEntity entity, Type stateType)
        {
            var binaryData = ReadBinaryData(entity);
            var stringData = ReadStringData(entity);

            object dataValue = null;
            try
            {
                if (binaryData.Length > 0)
                {
                    // Rehydrate
                    dataValue = this.serializationManager.DeserializeFromByteArray<object>(binaryData);
                }
                else if (!string.IsNullOrEmpty(stringData))
                {
                    dataValue = Newtonsoft.Json.JsonConvert.DeserializeObject(stringData, stateType, this.JsonSettings);
                }

                // Else, no data found
            }
            catch (Exception exc)
            {
                var sb = new StringBuilder();
                if (binaryData.Length > 0)
                {
                    sb.AppendFormat("Unable to convert from storage format GrainStateEntity.Data={0}", binaryData);
                }
                else if (!string.IsNullOrEmpty(stringData))
                {
                    sb.AppendFormat("Unable to convert from storage format GrainStateEntity.StringData={0}", stringData);
                }
                if (dataValue != null)
                {
                    sb.AppendFormat("Data Value={0} Type={1}", dataValue, dataValue.GetType());
                }

                logger.Error(0, sb.ToString(), exc);
                throw new AggregateException(sb.ToString(), exc);
            }

            return dataValue;
        }

        private string GetKeyString(GrainReference grainReference)
        {
            var key = String.Format("{0}_{1}", this.clusterOptions.ServiceId, grainReference.ToKeyString());
            return AzureTableUtils.SanitizeTableProperty(key);
        }

        private class GrainStateTableDataManager
        {
            public string TableName { get; private set; }
            private readonly AzureTableDataManager<TableEntity> tableManager;
            private readonly ILogger logger;

            public GrainStateTableDataManager(AzureStorageOperationOptions options, ILogger logger)
            {
                this.logger = logger;
                TableName = options.TableName;
                tableManager = new AzureTableDataManager<TableEntity>(options, logger);
            }

            public Task InitTableAsync()
            {
                return tableManager.InitTableAsync();
            }

            public async Task<TableEntity> Read(string partitionKey, string rowKey)
            {
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_Storage_Reading, "Reading: PartitionKey={0} RowKey={1} from Table={2}", partitionKey, rowKey, TableName);
                try
                {
                    var data = await tableManager.ReadSingleTableEntryAsync(partitionKey, rowKey).ConfigureAwait(false);
                    if (data.Entity == null)
                    {
                        if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_DataNotFound, "DataNotFound reading: PartitionKey={0} RowKey={1} from Table={2}", partitionKey, rowKey, TableName);
                        return default;
                    }
                    TableEntity stateEntity = data.Entity;
                    var record = stateEntity;
                    record.ETag = new ETag(data.ETag);
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_Storage_DataRead, "Read: PartitionKey={0} RowKey={1} from Table={2} with ETag={3}", stateEntity.PartitionKey, stateEntity.RowKey, TableName, record.ETag);
                    return record;
                }
                catch (Exception exc)
                {
                    if (AzureTableUtils.TableStorageDataNotFound(exc))
                    {
                        if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_DataNotFound, "DataNotFound reading (exception): PartitionKey={0} RowKey={1} from Table={2} Exception={3}", partitionKey, rowKey, TableName, LogFormatter.PrintException(exc));
                        return default;  // No data
                    }
                    throw;
                }
            }

            public async Task Write(TableEntity entity)
            {
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_Storage_Writing, "Writing: PartitionKey={0} RowKey={1} to Table={2} with ETag={3}", entity.PartitionKey, entity.RowKey, TableName, entity.ETag);
                string eTag = string.IsNullOrEmpty(entity.ETag.ToString()) ?
                    await tableManager.CreateTableEntryAsync(entity).ConfigureAwait(false) :
                    await tableManager.UpdateTableEntryAsync(entity, entity.ETag).ConfigureAwait(false);
                entity.ETag = new ETag(eTag);
            }

            public async Task Delete(TableEntity entity)
            {
                if (string.IsNullOrWhiteSpace(entity.ETag.ToString()))
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_DataNotFound, "Not attempting to delete non-existent persistent state: PartitionKey={0} RowKey={1} from Table={2} with ETag={3}", entity.PartitionKey, entity.RowKey, TableName, entity.ETag);
                    return;
                }

                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace((int)AzureProviderErrorCode.AzureTableProvider_Storage_Writing, "Deleting: PartitionKey={0} RowKey={1} from Table={2} with ETag={3}", entity.PartitionKey, entity.RowKey, TableName, entity.ETag);
                await tableManager.DeleteTableEntryAsync(entity, entity.ETag).ConfigureAwait(false);
                entity.ETag = default;
            }
        }

        /// <summary> Decodes Storage exceptions.</summary>
        public bool DecodeException(Exception e, out HttpStatusCode httpStatusCode, out string restStatus, bool getRESTErrors = false)
        {
            return AzureTableUtils.EvaluateException(e, out httpStatusCode, out restStatus, getRESTErrors);
        }

        private async Task Init(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();
            try
            {
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_InitProvider, $"AzureTableGrainStorage {name} initializing: {this.options.ToString()}");
                this.JsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(this.typeResolver, this.grainFactory), this.options.UseFullAssemblyNames, this.options.IndentJson, this.options.TypeNameHandling);

                this.options.ConfigureJsonSerializerSettings?.Invoke(this.JsonSettings);
                this.tableDataManager = new GrainStateTableDataManager(this.options, this.logger);
                await this.tableDataManager.InitTableAsync();
                stopWatch.Stop();
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_InitProvider, $"Initializing provider {this.name} of type {this.GetType().Name} in stage {this.options.InitStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
            }
            catch (Exception ex)
            {
                stopWatch.Stop();
                this.logger.LogError((int)ErrorCode.Provider_ErrorFromInit, $"Initialization failed for provider {this.name} of type {this.GetType().Name} in stage {this.options.InitStage} in {stopWatch.ElapsedMilliseconds} Milliseconds.", ex);
                throw;
            }
        }

        private Task Close(CancellationToken ct)
        {
            this.tableDataManager = null;
            return Task.CompletedTask;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<AzureTableGrainStorage>(this.name), this.options.InitStage, Init, Close);
        }
    }

    public static class AzureTableGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsSnapshot = services.GetRequiredService<IOptionsMonitor<AzureTableStorageOptions>>();
            var clusterOptions = services.GetProviderClusterOptions(name);
            return ActivatorUtilities.CreateInstance<AzureTableGrainStorage>(services, name, optionsSnapshot.Get(name), clusterOptions);
        }
    }
}
