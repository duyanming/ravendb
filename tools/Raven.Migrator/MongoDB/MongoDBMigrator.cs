﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using Newtonsoft.Json;

namespace Raven.Migrator.MongoDB
{
    public class MongoDBMigrator
    {
        private readonly MongoDBConfiguration _configuration;
        private readonly BsonDocument _filterDefinition = new BsonDocument();

        private const string MongoDocumentId = "_id";

        public MongoDBMigrator(MongoDBConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task GetDatabases()
        {
            AssertConnectionString();

            var databases = new List<string>();

            var client = CreateNewMongoClient();
            using (var cursor = await client.ListDatabasesAsync())
            {
                while (await cursor.MoveNextAsync())
                {
                    foreach (var database in cursor.Current)
                    {
                        var name = database["name"].ToString();
                        if (name.Equals("admin") || name.Equals("local"))
                            continue;

                        databases.Add(name);
                    }
                }
            }

            MigrationHelpers.OutputClass(_configuration,
                new DatabasesInfo
                {
                    Databases = databases
                });
        }

        public async Task GetCollectionsInfo()
        {
            AssertDatabaseName();

            var client = CreateNewMongoClient();
            var database = client.GetDatabase(_configuration.DatabaseName);

            MigrationHelpers.OutputClass(_configuration,
                new ExtendedCollectionsInfo
                {
                    Collections = await GetCollections(database),
                    HasGridFs = await HasGridFs(database)
                });
        }

        private static async Task<List<string>> GetCollections(IMongoDatabase database)
        {
            var collections = new List<string>();

            using (var cursor = await database.ListCollectionsAsync())
            {
                while (await cursor.MoveNextAsync())
                {
                    foreach (var collectionDocument in cursor.Current)
                    {
                        var collectionName = collectionDocument["name"].ToString();
                        if (collectionName.EndsWith(".chunks") || collectionName.EndsWith(".files"))
                            continue; // grid fs files will be handled separately

                        collections.Add(collectionName);
                    }
                }
            }

            return collections;
        }

        private static async Task<bool> HasGridFs(IMongoDatabase database)
        {
            var bucket = new GridFSBucket(database);

            var filter = Builders<GridFSFileInfo>.Filter.Empty;
            using (var cursor = await bucket.FindAsync(filter))
            {
                return await cursor.MoveNextAsync();
            }
        }

        public async Task MigrateSingleDatabse()
        {
            AssertDatabaseName();

            var client = CreateNewMongoClient();
            var database = client.GetDatabase(_configuration.DatabaseName);

            if (_configuration.CollectionsToMigrate == null || 
                _configuration.CollectionsToMigrate.Count == 0 && _configuration.MigrateGridFS == false)
            {
                _configuration.CollectionsToMigrate = await GetCollectionsToMigrate(database);
            }

            using (MigrationHelpers.GetStreamWriter(_configuration, out var streamWriter))
            {
                await streamWriter.WriteAsync("{\"Docs\":[");

                var isFirstDocument = new Reference<bool> { Value = true };
                foreach (var collection in _configuration.CollectionsToMigrate)
                {
                    var mongoCollectionName = collection.Key;
                    var ravenCollectionName = collection.Value;
                    if (string.IsNullOrWhiteSpace(ravenCollectionName))
                        ravenCollectionName = mongoCollectionName;

                    await MigrateSingleCollection(database, mongoCollectionName, ravenCollectionName, isFirstDocument, streamWriter);
                }

                if (_configuration.MigrateGridFS)
                    await MigrateGridFS(database, isFirstDocument, streamWriter);

                await streamWriter.WriteAsync("]}");
            }
        }

        private async Task MigrateSingleCollection(
            IMongoDatabase database, 
            string mongoCollectionName,
            string ravenCollectionName,
            Reference<bool> isFirstDocument,
            StreamWriter streamWriter)
        {
            var collection = database.GetCollection<ExpandoObject>(mongoCollectionName);

            using (var documentsCursor = await collection.Find(_filterDefinition).ToCursorAsync())
            {
                while (await documentsCursor.MoveNextAsync())
                {
                    foreach (var document in documentsCursor.Current)
                    {
                        await MigrationHelpers.WriteDocument(
                            document,
                            MongoDocumentId,
                            ravenCollectionName,
                            new List<string>
                            {
                                MongoDocumentId
                            },
                            isFirstDocument,
                            streamWriter);
                    }
                }
            }
        }

        private static async Task<Dictionary<string, string>> GetCollectionsToMigrate(IMongoDatabase database)
        {
            var collectionsToMigrate = new Dictionary<string, string>();

            using (var cursor = await database.ListCollectionsAsync())
            {
                while (await cursor.MoveNextAsync())
                {
                    foreach (var collectionDocument in cursor.Current)
                    {
                        var collectionName = collectionDocument["name"].ToString();
                        if (collectionName.EndsWith(".chunks") || collectionName.EndsWith(".files"))
                            continue; // grid fs files will be handled in a separate thread

                        collectionsToMigrate.Add(collectionName, collectionName);
                    }
                }
            }

            return collectionsToMigrate;
        }

        private static async Task MigrateGridFS(IMongoDatabase database, Reference<bool> isFirstDocument, StreamWriter streamWriter)
        {
            var bucket = new GridFSBucket(database);
            var collectionName = bucket.Options.BucketName == "fs" ? "Files" : bucket.Options.BucketName;
            var filter = Builders<GridFSFileInfo>.Filter.Empty;
            var attachmentNumber = 0;

            using (var cursor = await bucket.FindAsync(filter))
            {
                while (await cursor.MoveNextAsync())
                {
                    foreach (var fileInfo in cursor.Current)
                    {
                        var document = ToExpandoObject(fileInfo.Metadata);
                        var documentId = fileInfo.Id.ToString();
                        var totalSize = fileInfo.Length;
                        var contentType = GetContentType(fileInfo, (IDictionary<string, object>)document);

                        using (var stream = new MemoryStream())
                        {
                            //can't OpenDownloadStreamAsync because it's not a seeakble stream
                            await bucket.DownloadToStreamAsync(fileInfo.Id, stream);
                            stream.Position = 0;

                            attachmentNumber++;
                            await MigrationHelpers.WriteDocumentWithAttachment(
                                document, stream, totalSize, documentId, collectionName, 
                                contentType, attachmentNumber, isFirstDocument, streamWriter);
                        }
                    }
                }
            }
        }

        private static string GetContentType(GridFSFileInfo fileInfo, IDictionary<string, object> dictionary)
        {
            var contentType = fileInfo.ContentType;
            if (dictionary.TryGetValue("contentType", out object value))
            {
                contentType = (string)value;
            }
            else if (dictionary.TryGetValue("Content-Type", out value))
            {
                contentType = (string)value;
            }
            else if (dictionary.TryGetValue("ContentType", out value))
            {
                contentType = (string)value;
            }

            return contentType;
        }

        private static readonly System.Text.RegularExpressions.Regex ObjectIdReplace = new System.Text.RegularExpressions.Regex(@"ObjectId\((.[a-f0-9]{24}.)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static dynamic ToExpandoObject(BsonDocument bsonDocument)
        {
            var input = bsonDocument.ToJson();
            var json = ObjectIdReplace.Replace(input, s => s.Groups[1].Value);
            return JsonConvert.DeserializeObject<ExpandoObject>(json);
        }

        private void AssertConnectionString()
        {
            if (string.IsNullOrWhiteSpace(_configuration.ConnectionString))
                throw new ArgumentException("ConnectionString cannot be null or empty");
        }

        private void AssertDatabaseName()
        {
            if (string.IsNullOrWhiteSpace(_configuration.DatabaseName))
                throw new ArgumentException("DatabaseName cannot be null or empty");
        }

        private MongoClient CreateNewMongoClient()
        {
            return new MongoClient(_configuration.ConnectionString);
        }
    }
}
