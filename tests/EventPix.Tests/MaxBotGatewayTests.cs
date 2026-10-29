using System.Net;
using System.Text;
using EventPix.Web.Configuration;
using EventPix.Web.Notifications;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// Unit tests for the MAX REST gateway over a stubbed HttpMessageHandler: request shape
/// (URL, raw-token Authorization, JSON body), no-token behaviour, and long-poll update
/// normalisation with marker replay.
/// </summary>
public sealed class MaxBotGatewayTests
{
    private const string MeJson = """{"username":"eventpix_max","name":"EventPix"}""";

    private const string StartPage =
        """
        {"updates":[
            {"update_type":"bot_started","timestamp":1,"chat_id":42,"payload":"abc"},
            {"update_type":"message_created","timestamp":2,"message":{"recipient":{"chat_id":43,"chat_type":"dialog"},"body":{"text":"/start def"}}},
            {"update_type":"bot_stopped","timestamp":3,"chat_id":44}],
            "marker":99}
        """;

    private const string SecondPage =
        """{"updates":[{"update_type":"message_created","timestamp":4,"message":{"recipient":{"chat_id":45},"body":{"text":"/start xyz"}}}],"marker":100}""";

    [Fact]
    public async Task GetBotUsername_Returns_The_Username_Authorizing_With_The_Raw_Token()
    {
        using var api = new FakeMaxApi().RespondWith((200, MeJson));
        using var gateway = new MaxBotGateway(Cfg("tok-123"), api.Client);

        Assert.Equal("eventpix_max", await gateway.GetBotUsernameAsync(CancellationToken.None));

        var request = Assert.Single(api.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://platform-api2.max.ru/me", request.Uri.AbsoluteUri);
        Assert.Equal("tok-123", request.Authorization);   // raw token, no Bearer prefix
    }

    [Fact]
    public async Task GetBotUsername_Without_Token_Returns_Null_Without_Calling_The_Api()
    {
        using var api = new FakeMaxApi();
        using var gateway = new MaxBotGateway(Cfg(""), api.Client);

        Assert.Null(await gateway.GetBotUsernameAsync(CancellationToken.None));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Send_Posts_The_Text_To_The_Chat_Url()
    {
        using var api = new FakeMaxApi().RespondWith((200, "{}"));
        using var gateway = new MaxBotGateway(Cfg("tok-123"), api.Client);

        await gateway.SendAsync(42, "hello", CancellationToken.None);

        var request = Assert.Single(api.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://platform-api2.max.ru/messages?chat_id=42", request.Uri.AbsoluteUri);
        Assert.Equal("tok-123", request.Authorization);
        Assert.Equal("""{"text":"hello"}""", request.Body);
    }

    [Fact]
    public async Task Send_Without_Token_Is_A_Silent_NoOp()
    {
        using var api = new FakeMaxApi();
        using var gateway = new MaxBotGateway(Cfg(""), api.Client);

        await gateway.SendAsync(42, "hello", CancellationToken.None);

        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Send_Api_Error_Throws_So_The_Outbox_Backoff_Retries()
    {
        using var api = new FakeMaxApi().RespondWith((500, "{}"));
        using var gateway = new MaxBotGateway(Cfg("tok-123"), api.Client);

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => gateway.SendAsync(42, "hello", CancellationToken.None));
    }

    [Fact]
    public async Task Receive_Polls_With_Types_Normalises_Updates_And_Replays_The_Marker()
    {
        using var api = new FakeMaxApi().RespondWith((200, StartPage), (200, SecondPage));
        using var gateway = new MaxBotGateway(Cfg("tok-123"), api.Client);

        var seen = new List<(long ChatId, string? Text)>();
        using var done = new CancellationTokenSource();
        await gateway.ReceiveAsync(async (msg, _) =>
        {
            seen.Add((msg.ChatId, msg.Text));
            if (seen.Count == 3)
            {
                await done.CancelAsync();
            }

            await Task.CompletedTask;
        }, done.Token);

        Assert.Equal([(42L, "/start abc"), (43L, "/start def"), (45L, "/start xyz")], seen);

        // The first poll filters update types and long-polls without a marker; the next poll replays it.
        Assert.DoesNotContain("marker=", api.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("types=bot_started", api.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("types=message_created", api.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("timeout=30", api.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("marker=99", api.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_Carries_The_Sender_Identity_From_User_And_Sender_Fields()
    {
        // bot_started carries `user`; message_created carries `sender` (either id shape tolerated).
        const string page =
            """
            {"updates":[
                {"update_type":"bot_started","timestamp":1,"chat_id":42,"payload":"abc",
                 "user":{"user_id":777,"first_name":"Ada","last_name":"Lovelace","username":"ada"}},
                {"update_type":"message_created","timestamp":2,
                 "message":{"sender":{"id":9001,"name":"Grace Hopper","username":"grace"},
                            "recipient":{"chat_id":43},"body":{"text":"/start def"}}}],
             "marker":9}
            """;
        using var api = new FakeMaxApi().RespondWith((200, page));
        using var gateway = new MaxBotGateway(Cfg("tok-123"), api.Client);

        var seen = new List<BotMessage>();
        using var done = new CancellationTokenSource();
        await gateway.ReceiveAsync(async (msg, _) =>
        {
            seen.Add(msg);
            if (seen.Count == 2)
            {
                await done.CancelAsync();
            }

            await Task.CompletedTask;
        }, done.Token);

        Assert.Equal(42, seen[0].ChatId);
        Assert.Equal(777, seen[0].SenderId);
        Assert.Equal("Ada Lovelace", seen[0].SenderName);
        Assert.Equal("ada", seen[0].SenderUsername);
        Assert.Equal(43, seen[1].ChatId);
        Assert.Equal(9001, seen[1].SenderId);
        Assert.Equal("Grace Hopper", seen[1].SenderName);
    }

    [Fact]
    public async Task Receive_Without_Token_Returns_Immediately()
    {
        using var api = new FakeMaxApi();
        using var gateway = new MaxBotGateway(Cfg(""), api.Client);

        await gateway.ReceiveAsync((_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.Empty(api.Requests);
    }

    private static IOptions<MaxOptions> Cfg(string token)
    {
        return Options.Create(new MaxOptions { BotToken = token });
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

    /// <summary>Records every request and answers from a scripted (status, body) sequence.</summary>
    private sealed class FakeMaxApi : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = [];
        private (int Status, string Body)[] _script = [(200, "{}")];

        public FakeMaxApi()
        {
            Client = new HttpClient(this);
        }

        public HttpClient Client { get; }

        public List<CapturedRequest> Requests => _requests;

        public FakeMaxApi RespondWith(params (int Status, string Body)[] script)
        {
            _script = script;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = _script[Math.Min(_requests.Count, _script.Length - 1)];
            _requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null,
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
