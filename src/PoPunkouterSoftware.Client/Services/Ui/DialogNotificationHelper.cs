using Radzen;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Standardized Radzen <see cref="NotificationService"/> and <see cref="DialogService"/> helpers.
/// </summary>
public sealed class DialogNotificationHelper
{
    private readonly NotificationService _notifications;
    private readonly DialogService _dialogs;

    public DialogNotificationHelper(NotificationService notifications, DialogService dialogs)
    {
        _notifications = notifications;
        _dialogs = dialogs;
    }

    public void Success(string summary, string detail) =>
        _notifications.Notify(NotificationSeverity.Success, summary, detail);

    public void Warning(string summary, string detail) =>
        _notifications.Notify(NotificationSeverity.Warning, summary, detail);

    public void Error(string summary, string detail) =>
        _notifications.Notify(NotificationSeverity.Error, summary, detail);

    public async Task<bool> ConfirmAsync(string message, string title, string okText = "Confirm", string cancelText = "Cancel")
    {
        var confirmed = await _dialogs.Confirm(message, title, new ConfirmOptions
        {
            OkButtonText = okText,
            CancelButtonText = cancelText,
        });
        return confirmed == true;
    }
}
