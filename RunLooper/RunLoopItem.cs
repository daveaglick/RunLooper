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

namespace RunLooper
{
    /// <summary>
    /// Encapsulates a callback item (from the RunLoopSynchronizationContext or a RunLoopTaskScheduler). 
    /// </summary>
    internal abstract class RunLoopItem<TState, TResult> : IRunLoopItem
    {
        private readonly Func<TState, TResult> _func;
        private readonly TState _state;
        private readonly MethodInfo _method;
        private TResult _result;

        protected RunLoopItem(Func<TState, TResult> func, TState state, MethodInfo method)
        {
            _func = func;
            _state = state;
            _method = method ?? func.Method;
        }

        // The method implementors should use to execute the item
        // Sets the result of execution
        protected void ExecuteFunc()
        {
            _result = _func(_state);
        }

        // The method callers should use to execute the item
        public abstract void Execute();

        // Allows the implementing class to indicate if they're synchronous
        public abstract bool Synchronous { get; }

        public TResult Result
        {
            get { return _result; }
        }

        public MethodInfo Method
        {
            get { return _method; }
        }
    }
}
