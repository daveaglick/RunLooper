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
 */

//! @namespace RunLooper The primary RunLooper namespace.

#endregion

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
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

        private static bool _mono;

        private readonly Thread _thread;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private Action _cancelAction = null;
        private volatile MethodInfo _currentMethod = null;

        private readonly BlockingCollection<IRunLoopItem>[] _queues = new []
            {
                new BlockingCollection<IRunLoopItem>(new ConcurrentQueue<IRunLoopItem>()),
                new BlockingCollection<IRunLoopItem>(new ConcurrentQueue<IRunLoopItem>()),
                new BlockingCollection<IRunLoopItem>(new ConcurrentQueue<IRunLoopItem>())
            };

        private readonly RunLoopSynchronizationContext _synchronizationContext;
        private readonly TaskScheduler[] _taskSchedulers;

        static RunLoop()
        {
            _mono = (Type.GetType ("Mono.Runtime") != null); 
        }

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

        public int ManagedThreadId
        {
            get { return _thread.ManagedThreadId; }
        }

        public class MethodEventArgs : EventArgs
        {
            public MethodInfo Method { get; private set; }

            internal MethodEventArgs(MethodInfo method)
            {
                Method = method;
            }
        }

        public class EnqueuedEventArgs : MethodEventArgs
        {
            public Priority Priority { get; private set; }

            internal EnqueuedEventArgs(MethodInfo method, Priority priority)
                : base(method)
            {
                Priority = priority;
            }
        }

        public event EventHandler<EnqueuedEventArgs> Enqueued;
        public event EventHandler<MethodEventArgs> BeforeEvaluate;
        public event EventHandler<MethodEventArgs> AfterEvaluate;

        public int GetQueuedCount(Priority priority)
        {
            return _queues[(int) priority].Count;
        }

        public MethodInfo CurrentMethod
        {
            get { return _currentMethod; }
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

        // On cancel, this will continue to process until the queue is empty
        private void Run()
        {
            // Set the run loop thread's SynchronizationContext
            SynchronizationContext.SetSynchronizationContext(_synchronizationContext);

            // Loop until we cancel (and we already know we're on the main thread)
            while(Process(true)) {}

            // Carry out the final cancel action if there is one
            if (_cancelAction != null) _cancelAction();
        }

        // Processes one event in the queue and returns false if...
        // blocking and the queue was cancelled AND is empty
        // not blocking and the queue is empty
        private bool Process(bool block)
        {
            // Try to get an item off the queues in priority order without blocking
            IRunLoopItem item = null;
            if (_queues.FirstOrDefault(c => c.TryTake(out item)) == null)
            {
                // If we don't want to block or we've requested cancel, return false
                if (!block || _cancel.IsCancellationRequested) return false;

                // No items to take, so block until one of the queues gets an item
                try
                {
                    // Mono has a broken BlockingCollection<T>.TakeFromAny right now so we can't use it
                    // https://bugzilla.xamarin.com/show_bug.cgi?id=6095
                    if(_mono)
                    {
                        // This is an alternative that works under Mono
                        while (_queues.FirstOrDefault(c => c.TryTake(out item)) == null) { Thread.Sleep(0); }
                    }
                    else
                    {
                        BlockingCollection<IRunLoopItem>.TakeFromAny(_queues, out item, _cancel.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // If we've canceled, return true (so we can come around again to clear out the queue)
                    return true;
                }
            }

            // Execute the item (should never be null, but you never know)
            Evaluate(item);
            return true;
        }

        private void Evaluate(IRunLoopItem item)
        {
            if (item == null) return;
            if (BeforeEvaluate != null) BeforeEvaluate(this, new MethodEventArgs(item.Method));
            _currentMethod = item.Method;
            item.Execute();
            _currentMethod = null;
            if (AfterEvaluate != null) AfterEvaluate(this, new MethodEventArgs(item.Method));
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

            if(Enqueued != null) Enqueued(this, new EnqueuedEventArgs(item.Method, priority));

            // If we're on the main thread and this is a synchronous request, execute immediately
            // otherwise we'll get a deadlock while the caller waits for the item to process in
            // the queue but never returns control to the RunLoop to actually process the queue
            if(item.Synchronous && Thread.CurrentThread.ManagedThreadId == ManagedThreadId)
            {
                Evaluate(item);
                return;
            }

            // We're not on the main thread, queue for execution
            _queues[(int)priority].Add(item);
        }

        private void Enqueue<TState>(Func<TState, object> func, TState state, MethodInfo method, Priority priority)
        {
            Enqueue(new AsynchronousRunLoopItem<TState, object>(func, state, method), priority);
        }

        public void Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            Enqueue<object>(s => { action(); return null; }, null, action.Method, Priority.Normal);
        }

        public void Enqueue(Action action, Priority priority)
        {
            if (action == null) throw new ArgumentNullException("action");
            Enqueue<object>(s => { action(); return null; }, null, action.Method, priority);
        }

        public void Enqueue<TState>(Action<TState> action, TState state)
        {
            if (action == null) throw new ArgumentNullException("action");
            Enqueue(s => { action(s); return null; }, state, action.Method, Priority.Normal);
        }

        public void Enqueue<TState>(Action<TState> action, TState state, Priority priority)
        {
            if (action == null) throw new ArgumentNullException("action");
            Enqueue(s => { action(s); return null; }, state, action.Method, priority);
        }

        public void Execute(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            Execute<object, object>(s => { action(); return null; }, null, action.Method);
        }

        public void Execute<TState>(Action<TState> action, TState state)
        {
            if (action == null) throw new ArgumentNullException("action");
            Execute<TState, object>(s => { action(s); return null; }, state, action.Method);
        }

        public TResult Execute<TResult>(Func<TResult> func)
        {
            if (func == null) throw new ArgumentNullException("func");
            return Execute<object, TResult>(s => func(), null, func.Method);
        }

        public TResult Execute<TState, TResult>(Func<TState, TResult> func, TState state)
        {
            return Execute(func, state, func.Method);
        }

        private TResult Execute<TState, TResult>(Func<TState, TResult> func, TState state, MethodInfo method)
        {
            if (func == null) throw new ArgumentNullException("func");

            // Create an item and use a lambda to call the func and return it's result
            SynchronousRunLoopItem<TState, TResult> item
                = new SynchronousRunLoopItem<TState, TResult>(func, state, method);

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
