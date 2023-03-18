namespace DiscordFS.Helpers;

// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. 

public delegate Task AsyncEventHandler<in TEventArgs>(object sender, TEventArgs args);

public static class AsyncEventHandlerExtensions
{
    /// <summary>
    ///     Invokes asynchronous event handlers, returning an awaitable task. Each handler is fully executed
    ///     before the next handler in the list is invoked.
    /// </summary>
    /// <typeparam name="TEventArgs">The type of argument passed to each handler.</typeparam>
    /// <param name="handlers">The event handlers. May be <c>null</c>.</param>
    /// <param name="sender">The event source.</param>
    /// <param name="args">The event argument.</param>
    /// <returns>An awaitable task that completes when all handlers have completed.</returns>
    /// <exception cref="T:System.AggregateException">
    ///     Thrown if any handlers fail. It contains all
    ///     collected exceptions.
    /// </exception>
    public static async Task InvokeAsync<TEventArgs>(
        this AsyncEventHandler<TEventArgs> handlers,
        object sender,
        TEventArgs args)
    {
        if (handlers == null)
        {
            return;
        }

        List<Exception> exceptions = null;

        var listenerDelegates = handlers.GetInvocationList();
        foreach (var del in listenerDelegates)
        {
            var listenerDelegate = (AsyncEventHandler<TEventArgs>)del;

            try
            {
                await listenerDelegate(sender, args).ConfigureAwait(continueOnCapturedContext: true);
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>(capacity: 2);
                exceptions.Add(ex);
            }
        }

        // Throw collected exceptions, if any
        if (exceptions != null)
        {
            throw new AggregateException(exceptions);
        }
    }
}