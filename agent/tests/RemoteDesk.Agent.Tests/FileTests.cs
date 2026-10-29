using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent.Tests;

/// <summary>Файлы через HTTPS-часть агента: только во временных папках теста.</summary>
public sealed class FileTests : IAsyncLifetime
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "rdesk-files-" + Guid.NewGuid().ToString("N"));
    string Shared => Path.Combine(dir, "shared");
    string ReadOnly => Path.Combine(dir, "readonly");
    string Inbox => Path.Combine(dir, "inbox");
    readonly RecordingInput input = new();
    AgentHost host = null!;
    HttpClient http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Shared, "sub"));
        Directory.CreateDirectory(ReadOnly);
        Directory.CreateDirectory(Inbox);
        File.WriteAllText(Path.Combine(Shared, "hello.txt"), "Привет, мир");
        File.WriteAllText(Path.Combine(dir, "secret.txt"), "за пределами");

        host = new AgentHost(new AgentOptions(Path.Combine(dir, "agent"), Port: 0, Mdns: false, Loopback: true, Input: input));
        host.Shares.Update(
            [new ShareRoot("s", "Общая", Shared, true), new ShareRoot("r", "Только чтение", ReadOnly, false)],
            wholeComputer: false, wholeComputerWritable: false, inbox: Inbox);
        await host.StartAsync();

        var token = host.Devices.Add("phone-1", "Телефон", "android");
        var fp = host.Identity.Fingerprint;
        http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && AgentIdentity.FingerprintOf(cert.GetRawCertData()) == fp,
        })
        { BaseAddress = new Uri($"https://127.0.0.1:{host.Server.Port}/api/files/") };
        http.DefaultRequestHeaders.Add("X-RDesk-Device", "phone-1");
        http.DefaultRequestHeaders.Add("X-RDesk-Token", token);
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await host.DisposeAsync();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    async Task<JsonObject> Json(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonObject>())!;

    [Fact]
    public async Task Roots_and_listing()
    {
        var roots = await Json(await http.GetAsync("roots"));
        Assert.Equal(["s", "r"], roots["roots"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));

        var list = await Json(await http.GetAsync("list?root=s&path="));
        var names = list["entries"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).Order().ToList();
        Assert.Equal(["hello.txt", "sub"], names);
    }

    [Fact]
    public async Task Download_supports_ranges_for_resume()
    {
        var full = await http.GetByteArrayAsync("download?root=s&path=hello.txt");
        Assert.Equal("Привет, мир", Encoding.UTF8.GetString(full));

        using var request = new HttpRequestMessage(HttpMethod.Get, "download?root=s&path=hello.txt");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(4, null);
        using var partial = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(full[4..], await partial.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("..%2Fsecret.txt")]
    [InlineData("sub%2F..%2F..%2Fsecret.txt")]
    [InlineData("C:%2FWindows%2Fwin.ini")]
    public async Task Cannot_escape_shared_folder(string path)
    {
        var r = await http.GetAsync($"download?root=s&path={path}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Junction_inside_share_is_not_followed()
    {
        // Точка соединения внутри открытой папки, ведущая наружу (создаётся без прав администратора).
        var link = Path.Combine(Shared, "escape");
        Directory.CreateSymbolicLink(link, dir);
        var r = await http.GetAsync("download?root=s&path=escape%2Fsecret.txt");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Upload_resumes_after_interruption_and_renames_on_conflict()
    {
        var data = Encoding.UTF8.GetBytes("0123456789");
        var begin = await Json(await http.PostAsync("upload/begin?root=s&path=&name=hello.txt&conflict=rename", null));
        var name = begin["name"]!.GetValue<string>();
        Assert.Equal("hello (2).txt", name);
        Assert.Equal(0, begin["have"]!.GetValue<long>());

        // Первая часть, потом «обрыв».
        var first = await http.PutAsync($"upload?root=s&path=&name={Uri.EscapeDataString(name)}&size=10&offset=0", new ByteArrayContent(data[..4]));
        Assert.Equal(4, (await Json(first))["have"]!.GetValue<long>());
        Assert.False(File.Exists(Path.Combine(Shared, name)));

        // Неверное место продолжения — агент подсказывает правильное.
        var wrong = await http.PutAsync($"upload?root=s&path=&name={Uri.EscapeDataString(name)}&size=10&offset=2", new ByteArrayContent(data[2..]));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("offset:4", (await Json(wrong))["code"]!.GetValue<string>());

        // Повторный begin видит недокачанное и говорит, с какого места продолжать.
        var again = await Json(await http.PostAsync($"upload/begin?root=s&path=&name={Uri.EscapeDataString(name)}&conflict=rename", null));
        Assert.Equal(name, again["name"]!.GetValue<string>());
        Assert.Equal(4, again["have"]!.GetValue<long>());

        var rest = await Json(await http.PutAsync($"upload?root=s&path=&name={Uri.EscapeDataString(name)}&size=10&offset=4", new ByteArrayContent(data[4..])));
        Assert.True(rest["done"]!.GetValue<bool>());
        Assert.Equal("0123456789", File.ReadAllText(Path.Combine(Shared, name)));
        Assert.False(File.Exists(Path.Combine(Shared, name + FileService.PartSuffix)));
    }

    [Fact]
    public async Task Ask_conflict_and_read_only_folder()
    {
        var conflict = await http.PostAsync("upload/begin?root=s&path=&name=hello.txt&conflict=ask", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var readOnly = await http.PostAsync("upload/begin?root=r&path=&name=a.txt", null);
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        Assert.Equal("read_only", (await Json(readOnly))["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Mkdir_rename_delete()
    {
        Assert.True((await http.PostAsync("mkdir?root=s&path=Новая", null)).IsSuccessStatusCode);
        Assert.True((await http.PostAsync("rename?root=s&path=Новая&name=Готово", null)).IsSuccessStatusCode);
        Assert.True(Directory.Exists(Path.Combine(Shared, "Готово")));
        Assert.True((await http.PostAsync("delete?root=s&path=Готово", null)).IsSuccessStatusCode);
        Assert.False(Directory.Exists(Path.Combine(Shared, "Готово")));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("delete?root=s&path=", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("rename?root=s&path=hello.txt&name=a%2Fb", null)).StatusCode);
    }

    [Fact]
    public async Task Inbox_receives_shared_files_and_raises_event()
    {
        string? received = null;
        host.Files.Received += p => received = p;
        await http.PostAsync("upload/begin?root=inbox&path=&name=photo.jpg&conflict=rename", null);
        var r = await Json(await http.PutAsync("upload?root=inbox&path=&name=photo.jpg&size=3&offset=0", new ByteArrayContent([1, 2, 3])));
        Assert.True(r["done"]!.GetValue<bool>());
        Assert.Equal(Path.Combine(Inbox, "photo.jpg"), received);
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "roots");
        request.Headers.Add("X-RDesk-Device", "phone-1");
        http.DefaultRequestHeaders.Remove("X-RDesk-Token");
        request.Headers.Add("X-RDesk-Token", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
    }
}
