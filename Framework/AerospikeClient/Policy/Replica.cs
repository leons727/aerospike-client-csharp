﻿/* 
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
namespace Aerospike.Client
{
	/// <summary>
	/// Defines algorithm used to determine the target node for a command.
	/// The replica algorithm only affects single record commands.
	/// Batch, scan and query are not affected by replica algorithm.
	/// </summary>
	public enum Replica
	{
		/// <summary>
		/// Use node containing key's master partition.
		/// </summary>
		MASTER,

		/// <summary>
		/// Distribute reads across nodes containing key's master and replicated partitions
		/// in round-robin fashion.  Writes always use node containing key's master partition.
		/// <para>
		/// This option requires <see cref="Aerospike.Client.ClientPolicy.requestProleReplicas"/>
		/// to be enabled in order to function properly.
		/// </para>
		/// </summary>
		MASTER_PROLES,

		/// <summary>
		/// Try node containing master partition first.
		/// If connection fails, all commands try nodes containing replicated partitions.
		/// If socketTimeout is reached, reads also try nodes containing replicated partitions, 
		/// but writes remain on master node.
		/// <para>
		/// This option requires <see cref="Aerospike.Client.ClientPolicy.requestProleReplicas"/>
		/// to be enabled in order to function properly.
		/// </para>
		/// </summary>
		SEQUENCE,

		/// <summary>
		/// Distribute reads across all nodes in cluster in round-robin fashion.
		/// Writes always use node containing key's master partition.
		/// <para>
		/// This option is useful when the replication factor equals the number
		/// of nodes in the cluster and the overhead of requesting proles is not desired.
		/// </para>
		/// </summary>
		RANDOM
	}	
}
