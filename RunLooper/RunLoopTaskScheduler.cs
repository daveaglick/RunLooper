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
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RunLooper
{
    /// <summary>
    /// Provides task callbacks to the RunLoop for processing at a specified priority.
    /// Inline (synchronous) tasks are always executed at the highest priority.
    /// </summary>
    internal class RunLoopTaskScheduler : TaskScheduler
    {
        private readonly RunLoop _runLoop;
        private readonly RunLoop.Priority _priority;

        public RunLoopTaskScheduler(RunLoop runLoop, RunLoop.Priority priority)
        {
            _runLoop = runLoop;
            _priority = priority;
        }

        protected override void QueueTask(Task task)
        {
            // Create an item and use a lambda to call TryExecuteTask()
            AsynchronousRunLoopItem<object, object> item
                = new AsynchronousRunLoopItem<object, object>(
                    s => { TryExecuteTask(task); return null; }, null);

            // Queue up the item at the scheduler priority and do not wait for it to execute
            // Any unhandled exceptions should be caught by TryExecuteTask()
            _runLoop.Enqueue(item, _priority);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            // Create an item and use a lambda to call TryExecuteTask() and return it's result
            SynchronousRunLoopItem<object, object> item
                = new SynchronousRunLoopItem<object, object>(s => TryExecuteTask(task), null);

            // Queue up the item at the highest priority (since we'll be waiting for it to finish)
            _runLoop.Enqueue(item, RunLoop.Priority.High);

            // Wait for the RunLoop to execute it
            item.ManualResetEvent.WaitOne();

            // Rethrow any exceptions on the current thread
            // (though we shouldn't have any since TryExecuteTask should have handled them)
            if (item.Exception != null) throw item.Exception;

            // Return the result
            return (bool) (item.Result ?? false);
        }

        public override int MaximumConcurrencyLevel
        {
            get { return 1; }
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            throw new NotSupportedException();
        }

        protected override bool TryDequeue(Task task)
        {
            return false;
        }
    }
}
