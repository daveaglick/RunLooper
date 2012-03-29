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

#region Doxygen Comments

/** @mainpage RunLooper API Documentation
 * RunLooper is a utility library designed to help add a main thread run loop
 * to applications that lack one (such as console applications or libraries).
 * For more information, please see the RunLooper
 * portal at https://dracorp.assembla.com/spaces/runlooper.
 */

//! @namespace RunLooper The primary RunLooper namespace.

#endregion

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RunLooper
{
    // The code in these classes is partially based on concepts found here:
    // http://www.codeproject.com/Articles/32113/Understanding-SynchronizationContext-Part-II
    // http://blogs.msdn.com/b/pfxteam/archive/2012/01/20/10259049.aspx

    /// <summary>
    /// The main run loop. Responsible for queueing callbacks
    /// at various priorities and executing on the main thread accordingly.
    /// </summary>
    public class RunLoop : IDisposable
    {
        public enum Priority { High = 0, Normal = 1, Low = 2 }

        private readonly Thread _thread;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private Action _cancelAction = null;

        private readonly BlockingCollection<IRunLoopItem>[] _queues = new []
            {
                new BlockingCollection<IRunLoopItem>(new ConcurrentQueue<IRunLoopItem>()),
                new BlockingCollection<IRunLoopItem>(new ConcurrentQueue<IRunLoopItem>()),
                new BlockingCollection<IRunLoopItem>(new ConcurrentQueue<IRunLoopItem>())
            };

        private readonly RunLoopSynchronizationContext _synchronizationContext;
        private readonly TaskScheduler[] _taskSchedulers;

        public RunLoop() : this(null)
        {
        }

        public RunLoop(string threadName)
        {
            _thread = new Thread(Run);
            _thread.Name = String.IsNullOrEmpty(threadName) ? "RunLooper Thread" : threadName;
            _synchronizationContext = new RunLoopSynchronizationContext(this);
            _taskSchedulers = new []
                {
                    new RunLoopTaskScheduler(this, Priority.High),
                    new RunLoopTaskScheduler(this, Priority.Normal),
                    new RunLoopTaskScheduler(this, Priority.Low)
                };
        }

        internal int ManagedThreadId
        {
            get { return _thread.ManagedThreadId; }
        }

        public void Start()
        {
            _thread.Start();
        }

        public void Dispose()
        {
            Dispose(null);
        }

        public void Dispose(Action action)
        {
            if (_cancel.IsCancellationRequested) return;

            // Set a cancel action to be executed on the main thread right before it shuts down
            _cancelAction = action;

            // Signal for cancellation
            _cancel.Cancel();

            // Block while waiting for the run loop to finish
            _thread.Join();
        }

        private void Run()
        {
            // Set the run loop thread's SynchronizationContext
            SynchronizationContext.SetSynchronizationContext(_synchronizationContext);

            // Loop until we cancel (and we already know we're on the main thread)
            while(Process(true)) {}

            // Carry out the final cancel action if there is one
            if (_cancelAction != null) _cancelAction();
        }

        // Processes one event in the queue and returns false if the queue was cancelled
        private bool Process(bool block)
        {
            // If we've requested to cancel the run loop, just return
            if (_cancel.IsCancellationRequested) return false;
            
            // Try to get an item off the queues in priority order without blocking
            IRunLoopItem item = null;
            if (_queues.FirstOrDefault(c => c.TryTake(out item)) == null)
            {
                // If we don't want to block, just return
                if (!block) return false;

                // No items to take, so block until one of the queues gets an item
                try
                {
                    BlockingCollection<IRunLoopItem>.TakeFromAny(_queues, out item, _cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    // If we've canceled, return false
                    return false;
                }
            }

            // Execute the item
            item.Execute();
            return true;
        }

        /// <summary>
        /// Allows the RunLoop to continue processing events.
        /// This can only be called from the main thread (calling from another thread will always
        /// return false). This method should be used if the caller is on the main thread
        /// but needs to wait for another thread. Without calling this while waiting, the main
        /// thread will block for an extended period and events will pile up.
        /// </summary>
        /// <param name="block">Indicates if the calling thread should block until an event is available to process.</param>
        /// <returns>True if an event was processed (will always return true if block is true unless the RunLoop is disposed).</returns>
        public bool Yield(bool block)
        {
            // Make sure we're on the main thread
            return Thread.CurrentThread.ManagedThreadId == ManagedThreadId && Process(block);
        }

        /// <summary>
        /// Allows the RunLoop to continue processing events for a specified TimeSpan.
        /// This can only be called from the main thread (calling from another thread will always
        /// return immediately). This method should be used if the caller is on the main thread
        /// but needs to wait for another thread. Without calling this while waiting, the main
        /// thread will block for an extended period and events will pile up.
        /// This method might return after a longer time interval than the one specified if one
        /// of the methods in the run loop is long running.
        /// </summary>
        /// <param name="timeSpan">The amount of time to iterate the run loop. </param>
        public void Yield(TimeSpan timeSpan)
        {
            // Only iterate if on the main thread
            if (Thread.CurrentThread.ManagedThreadId != ManagedThreadId) return;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            while (stopwatch.Elapsed < timeSpan)
            {
                Process(false);
            }
            stopwatch.Stop();
        }

        /// <summary>
        /// Allows the RunLoop to continue processing events until a specified AutoResetEvent is signaled.
        /// This can only be called from the main thread (calling from another thread will always
        /// return immediately). This method should be used if the caller is on the main thread
        /// but needs to wait for another thread. Without calling this while waiting, the main
        /// thread will block for an extended period and events will pile up.
        /// This method might return some time after the specified AutoResetEvent is signaled if one
        /// of the methods in the run loop is long running.
        /// </summary>
        /// <param name="autoResetEvent">The AutoResetEvent to wait for a signal from.</param>
        public void Yield(AutoResetEvent autoResetEvent)
        {
            // Only iterate if on the main thread
            if (Thread.CurrentThread.ManagedThreadId != ManagedThreadId) return;

            while (!autoResetEvent.WaitOne(0))
            {
                Process(false);
            }
        }

        /// <summary>
        /// Allows the RunLoop to continue processing events until a specified AutoResetEvent is signaled
        /// or until a specified TimeSpan is reached (whichever comes first).
        /// This can only be called from the main thread (calling from another thread will always
        /// return immediately with the current signaled state of the AutoResetEvent). This method should
        /// be used if the caller is on the main thread
        /// but needs to wait for another thread. Without calling this while waiting, the main
        /// thread will block for an extended period and events will pile up.
        /// This method might return some time after the specified AutoResetEvent is signaled or after a
        /// longer time interval than the one specified if one of the methods in the run loop is long running.
        /// </summary>
        /// <param name="autoResetEvent">The auto reset event.</param>
        /// <param name="timeout">The time span.</param>
        /// <returns>true if the AutoResetEvent was signaled.</returns>
        public bool Yield(AutoResetEvent autoResetEvent, TimeSpan timeout)
        {
            // Only iterate if on the main thread
            if (Thread.CurrentThread.ManagedThreadId != ManagedThreadId)
                return autoResetEvent.WaitOne(0);

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            bool signaled;
            while (!(signaled = autoResetEvent.WaitOne(0)) && stopwatch.Elapsed < timeout)
            {
                Process(false);
            }
            stopwatch.Stop();
            return signaled;
        }

        public TaskScheduler TaskScheduler(Priority priority)
        {
            return _taskSchedulers[(int) priority];
        }

        public TaskScheduler NormalTaskScheduler
        {
            get { return TaskScheduler(Priority.Normal); }
        }

        internal void Enqueue(IRunLoopItem item, Priority priority)
        {
            if (_cancel.IsCancellationRequested) throw new ObjectDisposedException("RunLoop");
            if (item == null) throw new ArgumentNullException("item");

            // If we're on the main thread and this is a synchronous request, execute immediately
            // otherwise we'll get a deadlock while the caller waits for the item to process in
            // the queue but never returns control to the RunLoop to actually process the queue
            if(item.Synchronous && Thread.CurrentThread.ManagedThreadId == ManagedThreadId)
            {
                item.Execute();
                return;
            }

            // We're not on the main thread, queue for execution
            _queues[(int)priority].Add(item);
        }

        private void Enqueue<TState>(Func<TState, object> func, TState state, Priority priority)
        {
            Enqueue(new AsynchronousRunLoopItem<TState, object>(func, state), priority);
        }

        public void Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            Enqueue<object>(s => { action(); return null; }, null, Priority.Normal);
        }

        public void Enqueue(Action action, Priority priority)
        {
            if (action == null) throw new ArgumentNullException("action");
            Enqueue<object>(s => { action(); return null; }, null, priority);
        }

        public void Enqueue<TState>(Action<TState> action, TState state)
        {
            if (action == null) throw new ArgumentNullException("action");
            Enqueue(s => { action(s); return null; }, state, Priority.Normal);
        }

        public void Enqueue<TState>(Action<TState> action, TState state, Priority priority)
        {
            if (action == null) throw new ArgumentNullException("action");
            Enqueue(s => { action(s); return null; }, state, priority);
        }

        public void Execute(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            Execute<object, object>(s => { action(); return null; }, null);
        }

        public void Execute<TState>(Action<TState> action, TState state)
        {
            if (action == null) throw new ArgumentNullException("action");
            Execute<TState, object>(s => { action(s); return null; }, state);
        }

        public TResult Execute<TResult>(Func<TResult> func)
        {
            if (func == null) throw new ArgumentNullException("func");
            return Execute<object, TResult>(s => func(), null);
        }

        public TResult Execute<TState, TResult>(Func<TState, TResult> func, TState state)
        {
            if (func == null) throw new ArgumentNullException("func");

            // Create an item and use a lambda to call the func and return it's result
            SynchronousRunLoopItem<TState, TResult> item
                = new SynchronousRunLoopItem<TState, TResult>(func, state);

            // Queue up the item at the highest priority (since we'll be waiting for it to finish)
            Enqueue(item, Priority.High);

            // Wait for the RunLoop to execute it
            item.ManualResetEvent.WaitOne();

            // Rethrow any exceptions on the current thread
            if (item.Exception != null) throw item.Exception;

            // Return the result
            return item.Result;
        }
    }
}
