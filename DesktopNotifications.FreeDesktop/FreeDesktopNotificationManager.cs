﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Tmds.DBus;

namespace DesktopNotifications.FreeDesktop
{
    public class FreeDesktopNotificationManager : INotificationManager, IDisposable
    {
        private readonly FreeDesktopApplicationContext _appContext;
        private const string NotificationsService = "org.freedesktop.Notifications";

        private static readonly ObjectPath NotificationsPath = new ObjectPath("/org/freedesktop/Notifications");
        private readonly Dictionary<uint, Notification> _activeNotifications;
        private Connection? _connection;
        private IDisposable? _notificationActionSubscription;
        private IDisposable? _notificationCloseSubscription;

        private IFreeDesktopNotificationsProxy? _proxy;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="appContext"></param>
        public FreeDesktopNotificationManager(FreeDesktopApplicationContext? appContext = null)
        {
            _appContext = appContext ?? FreeDesktopApplicationContext.FromCurrentProcess();
            _activeNotifications = new Dictionary<uint, Notification>();
        }

        public void Dispose()
        {
            _notificationActionSubscription?.Dispose();
            _notificationCloseSubscription?.Dispose();
        }

        public event EventHandler<NotificationActivatedEventArgs>? NotificationActivated;
        public event EventHandler<NotificationDismissedEventArgs>? NotificationDismissed;

        public async ValueTask Initialize()
        {
            _connection = Connection.Session;

            await _connection.ConnectAsync();

            _proxy = _connection.CreateProxy<IFreeDesktopNotificationsProxy>(
                NotificationsService,
                NotificationsPath
            );

            _notificationActionSubscription = await _proxy.WatchActionInvokedAsync(
                OnNotificationActionInvoked,
                OnNotificationActionInvokedError
            );
            _notificationCloseSubscription = await _proxy.WatchNotificationClosedAsync(
                OnNotificationClosed,
                OnNotificationClosedError
            );
        }

        public async ValueTask ShowNotification(Notification notification, DateTimeOffset? expirationTime = null)
        {
            if (_connection == null || _proxy == null)
            {
                throw new InvalidOperationException("Not connected. Call Initialize() first.");
            }

            var duration = expirationTime - DateTimeOffset.Now;
            var actions = GenerateActions(notification);

            var id = await _proxy.NotifyAsync(
                _appContext.Name,
                0,
                _appContext.AppIcon ?? string.Empty,
                notification.Title ?? throw new ArgumentException(),
                notification.Body ?? throw new ArgumentException(),
                actions.ToArray(),
                new Dictionary<string, object> {{"urgency", 1}},
                duration?.Milliseconds ?? 0
            ).ConfigureAwait(false);

            _activeNotifications[id] = notification;
        }

        private static IEnumerable<string> GenerateActions(Notification notification)
        {
            foreach (var (title, actionId) in notification.Buttons)
            {
                yield return actionId;
                yield return title;
            }
        }

        private void OnNotificationClosedError(Exception obj)
        {
            throw obj;
        }

        private static NotificationDismissReason GetReason(uint reason)
        {
            return reason switch
            {
                1 => NotificationDismissReason.Expired,
                2 => NotificationDismissReason.User,
                3 => NotificationDismissReason.Application,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private void OnNotificationClosed((uint id, uint reason) @event)
        {
            _activeNotifications.Remove(@event.id, out var notification);
            Debug.Assert(notification != null);

            var dismissReason = GetReason(@event.reason);

            NotificationDismissed?.Invoke(this,
                new NotificationDismissedEventArgs(notification, dismissReason));
        }

        private void OnNotificationActionInvokedError(Exception obj)
        {
            throw obj;
        }

        private void OnNotificationActionInvoked((uint id, string actionKey) @event)
        {
            var notification = _activeNotifications[@event.id];

            NotificationActivated?.Invoke(this,
                new NotificationActivatedEventArgs(notification, @event.actionKey));
        }
    }
}