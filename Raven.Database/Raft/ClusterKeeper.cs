﻿// -----------------------------------------------------------------------
//  <copyright file="ClusterKeeper.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

using Rachis.Commands;
using Rachis.Transport;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Replication;
using Raven.Database.Plugins;
using Raven.Database.Raft.Dto;
using Raven.Database.Raft.Util;
using Raven.Json.Linq;
using Raven.Server;

namespace Raven.Database.Raft
{
	public class ClusterKeeper : IServerStartupTask
	{
		private DocumentDatabase systemDatabase;

		private RavenRaftEngine raftEngine;

		public void Execute(RavenDbServer server)
		{
			systemDatabase = server.SystemDatabase;
			raftEngine = server.Options.RaftEngine;

			systemDatabase.Notifications.OnDocumentChange += (db, notification, metadata) =>
			{
				if (string.Equals(notification.Id, Constants.Cluster.ClusterConfigurationDocumentKey, StringComparison.OrdinalIgnoreCase))
				{
					if (notification.Type != DocumentChangeTypes.Put)
						return;

					HandleClusterConfigurationChanges();
					return;
				}
			};

			raftEngine.Engine.TopologyChanged += HandleTopologyChanges;

			HandleClusterConfigurationChanges();
		}

		public void Dispose()
		{
		}

		private void HandleTopologyChanges(TopologyChangeCommand command)
		{
			if (command.Previous == null)
			{
				HandleClusterConfigurationChanges();
				return;
			}

			var removedNodes = command
				.Previous
				.AllNodeNames
				.Select(RaftHelper.GetNormalizedNodeUrl)
				.Except(command.Requested.AllNodeNames.Select(RaftHelper.GetNormalizedNodeUrl).ToList())
				.ToList();

			HandleClusterConfigurationChanges(removedNodes);
		}

		private void HandleClusterConfigurationChanges(List<string> removedNodes = null)
		{
			var configurationJson = systemDatabase.Documents.Get(Constants.Cluster.ClusterConfigurationDocumentKey, null);
			if (configurationJson == null)
				return;

			var configuration = configurationJson.DataAsJson.JsonDeserialization<ClusterConfiguration>();

			HandleClusterReplicationChanges(removedNodes, configuration.EnableReplication);
		}

		private void HandleClusterReplicationChanges(List<string> removedNodes, bool enableReplication)
		{
			var currentTopology = raftEngine.Engine.CurrentTopology;
			var replicationDocumentJson = systemDatabase.Documents.Get(Constants.Global.ReplicationDestinationsDocumentName, null);
			var replicationDocument = replicationDocumentJson != null
				? replicationDocumentJson.DataAsJson.JsonDeserialization<ReplicationDocument>()
				: new ReplicationDocument();

			var replicationDocumentNormalizedDestinations = replicationDocument
				.Destinations
				.ToDictionary(x => RaftHelper.GetNormalizedNodeUrl(x.Url), x => x);

			var currentTopologyNormalizedDestionations = currentTopology
				.AllNodes
				.ToDictionary(x => x.Uri.AbsoluteUri.ToLowerInvariant(), x => x);

			var urls = replicationDocumentNormalizedDestinations.Keys.Union(currentTopologyNormalizedDestionations.Keys).ToList();

			foreach (var url in urls)
			{
				ReplicationDestination destination;
				replicationDocumentNormalizedDestinations.TryGetValue(url, out destination);
				NodeConnectionInfo node;
				currentTopologyNormalizedDestionations.TryGetValue(url, out node);

				if (destination == null && node == null)
					continue; // not possible, but...

				if (destination != null && node == null)
				{
					if (removedNodes.Contains(destination.Url) == false)
						continue; // external destination

					replicationDocument.Destinations.Remove(destination);
					continue;
				}

				if (string.Equals(node.Name, raftEngine.Engine.Options.SelfConnection.Name, StringComparison.OrdinalIgnoreCase))
					continue; // skipping self

				if (destination == null)
				{
					destination = new ReplicationDestination();
					replicationDocument.Destinations.Add(destination);
				}

				destination.ApiKey = node.ApiKey;
				destination.Database = null;
				destination.Disabled = enableReplication == false;
				destination.Domain = node.Domain;
				//destination.Password = node.Password;
				destination.TransitiveReplicationBehavior = TransitiveReplicationOptions.Replicate;
				destination.Url = node.Uri.AbsoluteUri;
				destination.Username = node.Username;
			}

			systemDatabase.Documents.Put(Constants.Global.ReplicationDestinationsDocumentName, null, RavenJObject.FromObject(replicationDocument), new RavenJObject(), null);
		}
	}
}