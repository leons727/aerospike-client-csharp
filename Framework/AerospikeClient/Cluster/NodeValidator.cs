/* 
 * Copyright 2012-2018 Aerospike, Inc.
 *
 * Portions may be licensed to Aerospike, Inc. under one or more contributor
 * license agreements.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy of
 * the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations under
 * the License.
 */
using System;
using System.Net;
using System.Text;
using System.Collections.Generic;

namespace Aerospike.Client
{
	public sealed class NodeValidator
	{
		internal string name;
		internal List<Host> aliases;
		internal Host primaryHost;
		internal IPEndPoint primaryAddress;
		internal Connection primaryConn;
		internal byte[] sessionToken;
		internal DateTime? sessionExpiration;
		internal uint features;

		/// <summary>
		/// Add node(s) referenced by seed host aliases. In most cases, aliases reference
		/// a single node.  If round robin DNS configuration is used, the seed host may have
		/// several addresses that reference different nodes in the cluster.
		/// </summary>
		public void SeedNodes(Cluster cluster, Host host, Dictionary<string, Node> nodesToAdd)
		{
			IPAddress[] addresses = Connection.GetHostAddresses(host.name, cluster.connectionTimeout);
			Exception exception = null;
			bool found = false;

			// Try all addresses because they might point to different nodes.
			foreach (IPAddress address in addresses)
			{
				try
				{
					ValidateAddress(cluster, address, host.tlsName, host.port, true);
					found = true;

					if (! nodesToAdd.ContainsKey(name))
					{
						// New node found.
						// Only set aliases when they were not set by load balancer detection logic.
						if (this.aliases == null)
						{
							SetAliases(addresses, host.tlsName, host.port);
						}
						Node node = cluster.CreateNode(this);
						nodesToAdd[name] = node;
					}
					else
					{
						// Node already referenced. Close connection.
						primaryConn.Close();
					}
				}
				catch (Exception e)
				{
					// Log and continue to next address.
					if (Log.DebugEnabled())
					{
						Log.Debug("Address " + address + ' ' + host.port + " failed: " + Util.GetErrorMessage(e));
					}

					if (exception == null)
					{
						exception = e;
					}
				}
			}

			if (!found)
			{
				// Exception can't be null here because Connection.GetHostAddresses()
				// will throw exception if aliases length is zero.
				throw exception;
			}
		}

		/// <summary>
		/// Verify that a host alias references a valid node.
		/// </summary>
		public void ValidateNode(Cluster cluster, Host host)
		{
			IPAddress[] addresses = Connection.GetHostAddresses(host.name, cluster.connectionTimeout);
			Exception exception = null;

			foreach (IPAddress address in addresses)
			{
				try
				{
					ValidateAddress(cluster, address, host.tlsName, host.port, false);
					SetAliases(addresses, host.tlsName, host.port);
					return;
				}
				catch (Exception e)
				{
					// Log and continue to next address.
					if (Log.DebugEnabled())
					{
						Log.Debug("Address " + address + ' ' + host.port + " failed: " + Util.GetErrorMessage(e));
					}

					if (exception == null)
					{
						exception = e;
					}
				}
			}

			// Exception can't be null here because Connection.GetHostAddresses()
			// will throw exception if aliases length is zero.
			throw exception;
		}

		private void ValidateAddress(Cluster cluster, IPAddress address, string tlsName, int port, bool detectLoadBalancer)
		{
			IPEndPoint socketAddress = new IPEndPoint(address, port);
			Connection conn = (cluster.tlsPolicy != null) ?
				new TlsConnection(cluster.tlsPolicy, tlsName, socketAddress, cluster.connectionTimeout, cluster.maxSocketIdleMillis, null) :
				new Connection(socketAddress, cluster.connectionTimeout, cluster.maxSocketIdleMillis, null);

			try
			{
				if (cluster.user != null)
				{
					// Login
					AdminCommand admin = new AdminCommand(ThreadLocalData.GetBuffer(), 0);
					admin.Login(cluster, conn, out sessionToken, out sessionExpiration);

					if (cluster.tlsPolicy != null && cluster.tlsPolicy.forLoginOnly)
					{
						// Switch to using non-TLS socket.
						SwitchClear sc = new SwitchClear(cluster, conn, sessionToken);
						conn.Close();
						address = sc.clearAddress;
						socketAddress = sc.clearSocketAddress;
						conn = sc.clearConn;

						// Disable load balancer detection since non-TLS address has already
						// been retrieved via service info command.
						detectLoadBalancer = false;
					}
				}

				List<string> commands = new List<string>(5);
				commands.Add("node");
				commands.Add("partition-generation");
				commands.Add("features");

				bool hasClusterName = cluster.HasClusterName;

				if (hasClusterName)
				{
					commands.Add("cluster-name");
				}

				string addressCommand = null;

				if (detectLoadBalancer)
				{
					// Seed may be load balancer with changing address. Determine real address.
					addressCommand = (cluster.tlsPolicy != null) ?
						cluster.useServicesAlternate ? "service-tls-alt" : "service-tls-std" :
						cluster.useServicesAlternate ? "service-clear-alt" : "service-clear-std";

					commands.Add(addressCommand);
				}

				// Issue commands.
				Dictionary<string, string> map = Info.Request(conn, commands);

				// Node returned results.
				this.primaryHost = new Host(address.ToString(), tlsName, port);
				this.primaryAddress = socketAddress;
				this.primaryConn = conn;

				ValidateNode(map);
				ValidatePartitionGeneration(map);
				SetFeatures(map);

				if (hasClusterName)
				{
					ValidateClusterName(cluster, map);
				}

				if (addressCommand != null)
				{
					SetAddress(cluster, map, addressCommand, tlsName);
				}
			}
			catch (Exception)
			{
				conn.Close();
				throw;
			}
		}

		private void ValidateNode(Dictionary<string, string> map)
		{
			if (! map.TryGetValue("node", out this.name))
			{
				throw new AerospikeException.InvalidNode();
			}
		}				

		private void ValidatePartitionGeneration(Dictionary<string, string> map)
		{
			string genString;
			int gen;

			if (!map.TryGetValue("partition-generation", out genString))
			{
				throw new AerospikeException.InvalidNode();
			}

			try
			{
				gen = Convert.ToInt32(genString);
			}
			catch (Exception)
			{
				throw new AerospikeException.InvalidNode("Invalid partition-generation: " + genString);
			}

			if (gen == -1)
			{
				throw new AerospikeException.InvalidNode("Node " + this.name + ' ' + this.primaryHost + " is not yet fully initialized");
			}
		}
				
		private void SetFeatures(Dictionary<string, string> map)
		{
			try
			{
				string featuresString = map["features"];
				string[] list = featuresString.Split(';');

				foreach (string feature in list)
				{
					if (feature.Equals("geo"))
					{
						this.features |= Node.HAS_GEO;
					}
					else if (feature.Equals("float"))
					{
						this.features |= Node.HAS_DOUBLE;
					}
					else if (feature.Equals("batch-index"))
					{
						this.features |= Node.HAS_BATCH_INDEX;
					}
					else if (feature.Equals("replicas"))
					{
						this.features |= Node.HAS_REPLICAS;
					}
					else if (feature.Equals("replicas-all"))
					{
						this.features |= Node.HAS_REPLICAS_ALL;
					}
					else if (feature.Equals("peers"))
					{
						this.features |= Node.HAS_PEERS;
					}
					else if (feature.Equals("cluster-stable"))
					{
						this.features |= Node.HAS_CLUSTER_STABLE;
					}
				}
			}
			catch (Exception)
			{
				// Unexpected exception. Use defaults.
			}
		}

		private void ValidateClusterName(Cluster cluster, Dictionary<string, string> map)
		{
			string id;

			if (!map.TryGetValue("cluster-name", out id) || !cluster.clusterName.Equals(id))
			{
				throw new AerospikeException.InvalidNode("Node " + this.name + ' ' + this.primaryHost + ' ' +
						" expected cluster name '" + cluster.clusterName + "' received '" + id + "'");
			}
		}

		private void SetAddress(Cluster cluster, Dictionary<string, string> map, string addressCommand, string tlsName)
		{
			if (!map.TryGetValue(addressCommand, out var result) || result == null || result.Length == 0)
			{
				// Server does not support service level call (service-clear-std, ...).
				// Load balancer detection is not possible.
				return;
			}

			List<Host> hosts = Host.ParseServiceHosts(result);
			Host h;

			// Search real hosts for seed.
			foreach (Host host in hosts)
			{
				h = host;

				string alt;
				if (cluster.ipMap != null && cluster.ipMap.TryGetValue(h.name, out alt))
				{
					h = new Host(alt, h.port);
				}

				if (h.Equals(this.primaryHost))
				{
					// Found seed which is not a load balancer.
					return;
				}
			}

			// Seed not found, so seed is probably a load balancer.
			// Find first valid real host.
			foreach (Host host in hosts)
			{
				try
				{
					h = host;

					string alt;
					if (cluster.ipMap != null && cluster.ipMap.TryGetValue(h.name, out alt))
					{
						h = new Host(alt, h.port);
					}

					IPAddress[] addresses = Connection.GetHostAddresses(h.name, cluster.connectionTimeout);

					foreach (IPAddress address in addresses)
					{
						try
						{
							IPEndPoint socketAddress = new IPEndPoint(address, h.port);
							Connection conn = (cluster.tlsPolicy != null) ?
								new TlsConnection(cluster.tlsPolicy, tlsName, socketAddress, cluster.connectionTimeout, cluster.maxSocketIdleMillis, null) :
								new Connection(socketAddress, cluster.connectionTimeout, cluster.maxSocketIdleMillis, null);

							try
							{
								if (cluster.user != null)
								{
									AdminCommand admin = new AdminCommand(ThreadLocalData.GetBuffer(), 0);

									if (!admin.Authenticate(cluster, conn, this.sessionToken))
									{
										throw new AerospikeException("Authentication failed");
									}
								}

								// Authenticated connection.  Set real host.
								SetAliases(addresses, tlsName, h.port);
								this.primaryHost = new Host(address.ToString(), tlsName, h.port);
								this.primaryAddress = socketAddress;
								this.primaryConn.Close();
								this.primaryConn = conn;
								return;
							}
							catch (Exception)
							{
								conn.Close();
							}
						}
						catch (Exception)
						{
							// Try next address.
						}
					}
				}
				catch (Exception)
				{
					// Try next host.
				}
			}

			// Failed to find a valid address. IP Address is probably internal on the cloud
			// because the server access-address is not configured.  Log warning and continue
			// with original seed.
			if (Log.InfoEnabled())
			{
				Log.Info("Invalid address " + result + ". access-address is probably not configured on server.");
			}
		}
		
		private void SetAliases(IPAddress[] addresses, string tlsName, int port)
		{
			// Add capacity for current address aliases plus IPV6 address and hostname.
			this.aliases = new List<Host>(addresses.Length + 2);

			foreach (IPAddress address in addresses)
			{
				this.aliases.Add(new Host(address.ToString(), tlsName, port));
			}
		}
	}

	sealed class SwitchClear
	{
		internal IPAddress clearAddress;
		internal IPEndPoint clearSocketAddress;
		internal Connection clearConn;

		// Switch from TLS connection to non-TLS connection.
		internal SwitchClear(Cluster cluster, Connection conn, byte[] sessionToken)
		{
			// Obtain non-TLS addresses.
			string command = cluster.useServicesAlternate ? "service-clear-alt" : "service-clear-std";
			string result = Info.Request(conn, command);
			List<Host> hosts = Host.ParseServiceHosts(result);
			Host clearHost;

			// Find first valid non-TLS host.
			foreach (Host host in hosts)
			{
				try
				{
					clearHost = host;

					string alternativeHost;
					if (cluster.ipMap != null && cluster.ipMap.TryGetValue(clearHost.name, out alternativeHost))
					{
						clearHost = new Host(alternativeHost, clearHost.port);
					}

					IPAddress[] addresses = Connection.GetHostAddresses(clearHost.name, cluster.connectionTimeout);

					foreach (IPAddress ia in addresses)
					{
						try
						{
							clearAddress = ia;
							clearSocketAddress = new IPEndPoint(ia, clearHost.port);
							clearConn = new Connection(clearSocketAddress, cluster.connectionTimeout, cluster.maxSocketIdleMillis, null);

							try
							{
								AdminCommand admin = new AdminCommand(ThreadLocalData.GetBuffer(), 0);

								if (! admin.Authenticate(cluster, clearConn, sessionToken))
								{
									throw new AerospikeException("Authentication failed");
								}
								return; // Authenticated clear connection.
							}
							catch (Exception)
							{
								clearConn.Close();
							}
						}
						catch (Exception)
						{
							// Try next address.
						}
					}
				}
				catch (Exception)
				{
					// Try next host.
				}
			}
			throw new AerospikeException("Invalid non-TLS address: " + result);
		}
	}
}
