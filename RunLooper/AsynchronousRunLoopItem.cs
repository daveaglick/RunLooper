﻿/*
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
    /// Equivalent to SynchronizationContext.Post() or TaskScheduler.QueueTask().
    /// Unhandled exceptions will terminate the main RunLoop thread.
    /// </summary>
    internal class AsynchronousRunLoopItem<TState, TResult> : RunLoopItem<TState, TResult>
    {
        public AsynchronousRunLoopItem(Func<TState, TResult> func, TState state, MethodInfo method)
            : base(func, state, method)
        {
        }

        public override void Execute()
        {
            ExecuteFunc();
        }

        public override bool Synchronous
        {
            get { return false; }
        }
    }
}
