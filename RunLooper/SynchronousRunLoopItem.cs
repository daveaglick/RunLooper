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
using System.Reflection;
using System.Threading;

namespace RunLooper
{
    /// <summary>
    /// Equivalent to SynchronizationContext.Send() or TaskScheduler.TryExecuteTaskInline().
    /// Exceptions are thrown in the context of the calling thread.
    /// </summary>
    internal class SynchronousRunLoopItem<TState, TResult> : RunLoopItem<TState, TResult>
    {
        private readonly ManualResetEvent _manualResetEvent = new ManualResetEvent(false);
        private Exception _exception = null;

        public SynchronousRunLoopItem(Func<TState, TResult> func, TState state, MethodInfo method)
            : base(func, state, method)
        {
        }

        public override void Execute()
        {
            try
            {
                ExecuteFunc();
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
            finally
            {
                _manualResetEvent.Set();
            }
        }

        public ManualResetEvent ManualResetEvent
        {
            get { return _manualResetEvent; }
        }

        public Exception Exception
        {
            get { return _exception; }
        }

        public override bool Synchronous
        {
            get { return true; }
        }
    }
}
