using System.Collections.Concurrent;
using System.Windows;
using GManager.Models;

namespace GManager.Core;

#region Event Definitions

public record AccountAddedEvent(GoogleAccount Account);
public record AccountRemovedEvent(string AccountId);
public record AccountUpdatedEvent(GoogleAccount Account);
public record AccountStateChangedEvent(string AccountId, AccountState NewState);
public record SyncCompletedEvent(string AccountId);
public record SyncFailedEvent(string AccountId, string ErrorMessage);
public record NavigateToAccountEvent(string AccountId, string? MessageId = null);
public record NewMailReceivedEvent(string AccountId, string AccountEmail, MailMessage Message);

#endregion

/// <summary>
/// Thread-safe in-memory event bus with automatic marshaling to WPF Dispatcher when required.
/// </summary>
public interface IEventAggregator
{
    void Subscribe<TEvent>(Action<TEvent> handler);
    void Unsubscribe<TEvent>(Action<TEvent> handler);
    void Publish<TEvent>(TEvent message);
}

public sealed class EventAggregator : IEventAggregator
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _subscribers = new();
    private readonly object _lock = new();

    public void Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var eventType = typeof(TEvent);

        lock (_lock)
        {
            var list = _subscribers.GetOrAdd(eventType, _ => new List<Delegate>());
            if (!list.Contains(handler))
            {
                list.Add(handler);
            }
        }
    }

    public void Unsubscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var eventType = typeof(TEvent);

        lock (_lock)
        {
            if (_subscribers.TryGetValue(eventType, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                {
                    _subscribers.TryRemove(eventType, out _);
                }
            }
        }
    }

    public void Publish<TEvent>(TEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var eventType = typeof(TEvent);

        List<Delegate> handlersCopy;
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(eventType, out var list) || list.Count == 0)
            {
                return;
            }
            handlersCopy = [.. list];
        }

        var dispatcher = Application.Current?.Dispatcher;

        foreach (var handler in handlersCopy)
        {
            if (handler is Action<TEvent> action)
            {
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(() => action(message));
                }
                else
                {
                    action(message);
                }
            }
        }
    }
}
