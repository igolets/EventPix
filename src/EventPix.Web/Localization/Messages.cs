using EventPix.Web.Domain;

namespace EventPix.Web.Localization;

/// <summary>Languages the server-side message catalogue ships. The browser side lives in the theme's strings templates.</summary>
internal enum MessageLanguage
{
    English,
    Russian,
}

/// <summary>
/// Every text the server composes itself: OTP e-mail, new-media notifications and bot replies.
/// English is the default and must stay byte-identical to the historical texts; the language comes
/// from EventPix:Language (deployment-wide, because e-mail and bot chats carry no language signal).
/// Channels and services never hard-code words - they ask here.
/// </summary>
internal static class Messages
{
    /// <summary>Matches exact tags ("ru") and regional ones ("ru-RU"); anything else falls back to English.</summary>
    public static MessageLanguage Resolve(string? language)
    {
        var tag = (language ?? string.Empty).Trim().ToLowerInvariant();
        return tag == "ru" || tag.StartsWith("ru-", StringComparison.Ordinal)
            ? MessageLanguage.Russian
            : MessageLanguage.English;
    }

    // ---- OTP e-mail ----

    public static string OtpSubject(MessageLanguage lang, string eventName) => lang switch
    {
        MessageLanguage.Russian => $"Код для входа в «{eventName}»",
        _ => $"Your sign-in code for {eventName}",
    };

    public static string OtpBody(MessageLanguage lang, string code, int minutes) => lang switch
    {
        MessageLanguage.Russian =>
            $"Ваш код: {code}\n\nКод действует {minutes} мин. Если вы его не запрашивали, просто проигнорируйте это письмо.",
        _ => $"Your code: {code}\n\nIt is valid for {minutes} minutes. " +
            "If you did not request it, ignore this message.",
    };

    // ---- New-media notifications (one text for every channel; Composer only assembles) ----

    public static string NotifySubject(MessageLanguage lang, string eventName, int updates) => lang switch
    {
        MessageLanguage.Russian when updates == 1 => $"Новое в «{eventName}»",
        MessageLanguage.Russian => $"Новое в «{eventName}» — обновлений: {updates}",
        _ when updates == 1 => $"New in {eventName}",
        _ => $"New in {eventName} ({updates} updates)",
    };

    public static string NotifyGroupLine(MessageLanguage lang, string uploader, string tab, int photos, int videos) =>
        lang switch
        {
            MessageLanguage.Russian => $"Новые материалы в альбоме «{tab}» от {uploader}: {photos} фото, {videos} видео.",
            _ => $"{uploader} added {photos} photo(s) and {videos} video(s) to \"{tab}\".",
        };

    public static string NotifyFooter(MessageLanguage lang, string url, string unsubscribeUrl) => lang switch
    {
        MessageLanguage.Russian => $"\n\nОткрыть: {url}\n\nОтписаться: {unsubscribeUrl}",
        _ => $"\n\nOpen: {url}\n\nUnsubscribe: {unsubscribeUrl}",
    };

    // ---- Bot linking and sign-in ----

    public static string BotInvalidCode(MessageLanguage lang) => lang switch
    {
        MessageLanguage.Russian =>
            "Эта ссылка недействительна или срок её действия истёк. " +
            "Откройте альбом, нажмите «Сообщить мне» и создайте новую ссылку.",
        _ => "This linking code is not valid or has expired. " +
            "Open the album, tap Notify me and generate a new link.",
    };

    public static string BotNotInvited(MessageLanguage lang) => lang switch
    {
        MessageLanguage.Russian =>
            "Этот альбом доступен только по приглашениям, и вас в списке нет. " +
            "Попросите организатора добавить ваш ник в мессенджере или один раз войдите по приглашённой почте.",
        _ => "This album is invite-only and this account is not on the list. " +
            "Ask the organiser to add your messenger handle, or sign in with your invited e-mail once.",
    };

    public static string BotScopeAll(MessageLanguage lang) => lang switch
    {
        MessageLanguage.Russian => "все альбомы",
        _ => "all albums",
    };

    public static string BotScopeRemoved(MessageLanguage lang) => lang switch
    {
        MessageLanguage.Russian => "удалённый альбом",
        _ => "a removed album",
    };

    public static string BotScopeTab(MessageLanguage lang, string tabName) => lang switch
    {
        MessageLanguage.Russian => $"альбом «{tabName}»",
        _ => $"album \u201c{tabName}\u201d",
    };

    public static string BotCadence(MessageLanguage lang, NotifyMode mode) => lang switch
    {
        MessageLanguage.Russian when mode == NotifyMode.Digest => "раз в день дайджестом",
        MessageLanguage.Russian => "сразу после добавления",
        _ when mode == NotifyMode.Digest => "once a day as a digest",
        _ => "as soon as it is added",
    };

    public static string BotSubscribed(MessageLanguage lang, string eventName, string scope, string cadence) =>
        lang switch
        {
            MessageLanguage.Russian =>
                $"Готово! Буду сообщать о новых материалах в «{eventName}» ({scope}) {cadence}.",
            _ => $"Done! I will tell you about new media in \u201c{eventName}\u201d ({scope}) {cadence}.",
        };

    public static string BotSignedIn(MessageLanguage lang, string eventName, string displayName) => lang switch
    {
        MessageLanguage.Russian =>
            $"Вы вошли в «{eventName}» как {displayName}. " +
            "Вернитесь во вкладку браузера, откуда начали, — она завершит вход сама.",
        _ => $"Signed in to \u201c{eventName}\u201d as {displayName}. " +
            "Return to the browser tab where you started; it will finish by itself.",
    };
}
