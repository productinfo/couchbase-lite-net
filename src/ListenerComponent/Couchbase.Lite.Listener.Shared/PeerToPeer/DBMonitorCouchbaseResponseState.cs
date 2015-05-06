﻿//
//  DBMonitorCouchbaseResponseState.cs
//
//  Author:
//  	Jim Borden  <jim.borden@couchbase.com>
//
//  Copyright (c) 2015 Couchbase, Inc All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//

using System.Text;
using System.Threading;

using Couchbase.Lite.Internal;
using Couchbase.Lite.Replicator;
using Couchbase.Lite.Util;

namespace Couchbase.Lite.Listener
{

    /// <summary>
    /// This class will wait for the database to change before writing to and
    /// possibly closing the HTTP response
    /// </summary>
    internal class DBMonitorCouchbaseResponseState : ICouchbaseResponseState
    {

        #region Constants

        private const string TAG = "DBMonitorCouchbaseResponseState";

        #endregion

        #region Variables

        private Database _db;
        private Timer _heartbeatTimer;
        private RevisionList _changes = new RevisionList();

        #endregion

        #region Properties

        /// <summary>
        /// The changes feed mode being used to listen to the database
        /// </summary>
        public ChangesFeedMode ChangesFeedMode { get; set; }

        /// <summary>
        /// Whether or not to write the document properties along with the changes
        /// </summary>
        public bool ChangesIncludeDocs { get; set; }

        /// <summary>
        /// Whether or not to include conflict revisions in the changes
        /// </summary>
        public bool ChangesIncludeConflicts { get; set; }

        /// <summary>
        /// The delegate to filter the changes being written
        /// </summary>
        public FilterDelegate ChangesFilter { get; set; }

        //ICouchbaseResponseState
        public CouchbaseLiteResponse Response { get; set; }

        //ICouchbaseResponseState
        public bool IsAsync { get; set; }

        #endregion

        #region COnstructors

        /// <summary>
        /// Constructor
        /// </summary>
        public DBMonitorCouchbaseResponseState() 
        {
            IsAsync = false;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="response">The response to write to</param>
        public DBMonitorCouchbaseResponseState(CouchbaseLiteResponse response) : this()
        {
            Response = response;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Subscribes this object to the given database's <c>Changed</c> event for
        /// processing
        /// </summary>
        /// <param name="db">Db.</param>
        public void SubscribeToDatabase(Database db)
        {
            if (db == null) {
                return;
            }

            IsAsync = true;
            _db = db;
            _db.Changed += DatabaseChanged;
        }

        /// <summary>
        /// Starts a timer for writing heartbeat messages to the client
        /// </summary>
        /// <param name="response">The message to write</param>
        /// <param name="interval">The interval at which to write the message (in milliseconds)</param>
        public void StartHeartbeat(string response, int interval)
        {
            if (interval <= 0 || _heartbeatTimer != null) {
                return;
            }

            IsAsync = true;
            Response.WriteHeaders();
            _heartbeatTimer = new Timer(SendHeartbeatResponse, Encoding.UTF8.GetBytes(response), interval, interval);
        }

        #endregion

        #region Private Methods

        // Attempts to write the heartbeat message to the client
        private void SendHeartbeatResponse(object state)
        {
            if (!Response.WriteData((byte[])state, false)) {
                if (_heartbeatTimer != null) {
                    _heartbeatTimer.Dispose();
                    _heartbeatTimer = null;
                }
            }
        }

        // Processes a change in the subscribed database
        private void DatabaseChanged(object sender, DatabaseChangeEventArgs args)
        {
            foreach (var change in args.Changes) {
                var rev = change.AddedRevision;
                var winningRev = change.WinningRevision;

                if (!ChangesIncludeConflicts) {
                    if (winningRev == null) {
                        continue; // this change doesn't affect the winning rev ID, no need to send it
                    }

                    if (rev.Equals(winningRev)) {
                        // This rev made a _different_ rev current, so substitute that one.
                        // We need to emit the current sequence # in the feed, so put it in the rev.
                        // This isn't correct internally (this is an old rev so it has an older sequence)
                        // but consumers of the _changes feed don't care about the internal state.
                        if (ChangesIncludeDocs) {
                            _db.LoadRevisionBody(rev, DocumentContentOptions.None);
                        }
                    }
                }

                if (!_db.RunFilter(ChangesFilter, null, rev)) {
                    continue;
                }

                if (ChangesFeedMode == ChangesFeedMode.LongPoll) {
                    _changes.Add(rev);
                } else {
                    Log.D(TAG, "Sending continuous change chunk");
                    var written = Response.SendContinuousLine(DatabaseMethods.ChangesDictForRev(rev, this), ChangesFeedMode);
                    if (!written) {
                        Terminate();
                    }
                }
            }

            if (ChangesFeedMode == ChangesFeedMode.LongPoll && _changes.Count > 0) {
                var body = new Body(DatabaseMethods.ResponseBodyForChanges(_changes, 0, this));
                Response.WriteData(body.AsJson(), true);
                CouchbaseLiteRouter.ResponseFinished(this);
            }
        }

        // Tear down this object because an error occurred
        private void Terminate()
        {
            if (_db == null) {
                return;
            }

            _db.Changed -= DatabaseChanged;
            CouchbaseLiteRouter.ResponseFinished(this);
            _db = null;

            if (_heartbeatTimer != null) {
                _heartbeatTimer.Dispose();
                _heartbeatTimer = null;
            }
        }

        #endregion

    }
}
