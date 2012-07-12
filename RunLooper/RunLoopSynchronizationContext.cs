/*
 * Copyright 2012 WildCard, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 * 
 */

using System;
using System.Threading;

namespace RunLooper
{
    /// <summary>
    /// Provides a SynchronizationContext that adds callbacks to the RunLoop for processing.
    /// </summary>
    internal class RunLoopSynchronizationContext : SynchronizationContext
    {
        private readonly RunLoop _runLoop;

        public RunLoopSynchronizationContext(RunLoop runLoop)
        {
            _runLoop = runLoop;
        }

        public override void Send(SendOrPostCallback callback, object state)
        {
            if (callback == null) throw new ArgumentNullException("callback");

            // Create an item and use a lambda to convert the SendOrPostCallback to a Func<object,object>
            SynchronousRunLoopItem<object, object> item
                = new SynchronousRunLoopItem<object, object>(s => { callback(s); return null; }, state, callback.Method);

            // Queue up the item at the highest priority (since we'll be waiting for it to finish)
            _runLoop.Enqueue(item, RunLoop.Priority.High);

            // Wait for the RunLoop to execute it
            item.ManualResetEvent.WaitOne();

            // Rethrow any exceptions on the current thread
            if (item.Exception != null) throw item.Exception;
        }

        public override void Post(SendOrPostCallback callback, object state)
        {
            if (callback == null) throw new ArgumentNullException("callback");

            // Create an item and use a lambda to convert the SendOrPostCallback to a Func<object,object>
            AsynchronousRunLoopItem<object, object> item
                = new AsynchronousRunLoopItem<object, object>(s => { callback(s); return null; }, state, callback.Method);
            
            // Queue up the item and do not wait for it to execute
            // Any unhandled exceptions will crash the main thread
            _runLoop.Enqueue(item, RunLoop.Priority.Normal);
        }
    }
}
