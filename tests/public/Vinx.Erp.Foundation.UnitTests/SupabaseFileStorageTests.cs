using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Paguai;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class SupabaseFileStorageTests
{
    [Theory]
    [InlineData("")]
    [InlineData("provider-body-parser-diagnostic")]
    public async Task Duplicate_create_with_invalid_lookup_json_throws_only_sanitized_statuses_and_container(
        string responseBody)
    {
        using var handler = new SequencedResponseHandler(
            new HttpResponseMessage(HttpStatusCode.Conflict),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = CreateStorage(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.EnsurePrivateContainerAsync(CancellationToken.None));

        Assert.Equal(
            "Supabase Storage container provisioning failed with create status 409, lookup status 200, container vinx-foundation-private.",
            exception.Message);
    }

    [Fact]
    public async Task Signed_urls_and_object_operations_use_the_private_container()
    {
        using var handler = new RequestSequenceHandler(
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "/storage/v1/object/upload/sign/vinx-foundation-private/quarantine/company/document",
                    request.RequestUri!.AbsolutePath);
                Assert.Equal("{\"upsert\":false}", ReadBody(request));
                return JsonResponse("{\"signedURL\":\"/signed-upload\"}");
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "/storage/v1/object/sign/vinx-foundation-private/quarantine/company/document",
                    request.RequestUri!.AbsolutePath);
                return JsonResponse("{\"signedUrl\":\"/signed-download\"}");
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/storage/v1/object/list/vinx-foundation-private", request.RequestUri!.AbsolutePath);
                Assert.Equal("{\"prefix\":\"quarantine\",\"limit\":1000,\"offset\":0}", ReadBody(request));
                return JsonResponse(ObjectPage(0, 1000));
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/storage/v1/object/list/vinx-foundation-private", request.RequestUri!.AbsolutePath);
                Assert.Equal("{\"prefix\":\"quarantine\",\"limit\":1000,\"offset\":1000}", ReadBody(request));
                return JsonResponse(ObjectPage(1000, 1));
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal(
                    "/storage/v1/object/vinx-foundation-private/quarantine/company/document",
                    request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = CreateStorage(client);

        var upload = await storage.CreateSignedUploadUrlAsync("quarantine/company/document", CancellationToken.None);
        var download = await storage.CreateSignedDownloadUrlAsync(
            "quarantine/company/document", TimeSpan.FromMinutes(1), CancellationToken.None);
        var objects = await storage.ListObjectsAsync("quarantine", CancellationToken.None);
        await storage.DeleteObjectAsync("quarantine/company/document", CancellationToken.None);

        Assert.Equal("https://storage.example.test/signed-upload", upload.Url.AbsoluteUri);
        Assert.Equal("https://storage.example.test/signed-download", download.Url.AbsoluteUri);
        Assert.Equal(1001, objects.Count);
        Assert.Equal("quarantine/company/0000", objects[0].Key);
        Assert.Equal("quarantine/company/1000", objects[^1].Key);
    }

    [Fact]
    public void Upload_contract_does_not_accept_a_caller_selected_lifetime()
    {
        var method = typeof(FileStorage).GetMethod(nameof(FileStorage.CreateSignedUploadUrlAsync));

        Assert.NotNull(method);
        Assert.Equal(2, method.GetParameters().Length);
    }

    [Fact]
    public void Object_metadata_contract_does_not_claim_an_unavailable_creation_time()
    {
        Assert.Null(typeof(StorageObjectMetadata).GetProperty("CreatedAt"));
    }

    [Fact]
    public async Task Inspection_recalculates_metadata_and_reports_an_unavailable_malware_scan()
    {
        var payload = Encoding.UTF8.GetBytes("document payload");
        using var handler = new RequestSequenceHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/storage/v1/object/vinx-foundation-private/quarantine/company/document",
                request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") }
                }
            };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = CreateStorage(client);

        var inspection = await storage.InspectObjectAsync("quarantine/company/document", CancellationToken.None);

        Assert.Equal(MalwareScanStatus.Unavailable, inspection.MalwareScanStatus);
        Assert.Equal(payload.LongLength, inspection.Metadata.SizeBytes);
        Assert.Equal("application/pdf", inspection.Metadata.ContentType);
    }

    [Fact]
    public async Task Inspection_captures_the_leading_magic_bytes_of_the_object()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x0D };
        using var handler = new RequestSequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") }
            }
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = CreateStorage(client);

        var inspection = await storage.InspectObjectAsync("quarantine/company/document", CancellationToken.None);

        Assert.Equal(payload.Take(8).ToArray(), inspection.LeadingBytes.ToArray());
    }

    [Fact]
    public void Paguai_proof_link_lifetime_never_exceeds_the_storage_download_cap()
    {
        // Contrato entre módulos: o TTL pedido pelo Paguai precisa caber no teto real
        // do provedor — acima dele toda assinatura de link falha fechado (503).
        Assert.True(
            PaguaiDraftService.ProofLinkLifetime <= SupabaseFileStorage.MaximumDownloadUrlLifetime,
            $"proof link lifetime {PaguaiDraftService.ProofLinkLifetime} exceeds storage cap {SupabaseFileStorage.MaximumDownloadUrlLifetime}");
    }

    [Fact]
    public async Task Inspection_rejects_an_object_larger_than_the_hard_25MiB_limit()
    {
        using var handler = new RequestSequenceHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[DocumentUploadPolicy.MaxSizeBytes + 1])
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") }
            }
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = CreateStorage(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.InspectObjectAsync("quarantine/company/document", CancellationToken.None));
    }

    [Fact]
    public async Task Open_read_returns_the_content_stream_without_buffering()
    {
        var payload = Encoding.UTF8.GetBytes("streamed document payload");
        var content = new NonBufferedContent(payload);
        using var handler = new RequestSequenceHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/storage/v1/object/vinx-foundation-private/quarantine/company/document",
                request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = CreateStorage(client);

        await using var stream = await storage.OpenReadAsync("quarantine/company/document", CancellationToken.None);
        using var read = new MemoryStream();
        await stream.CopyToAsync(read);

        Assert.Equal(payload, read.ToArray());
        // Com ResponseHeadersRead o HttpClient não materializa o corpo: SerializeToStreamAsync
        // só roda se a opção de completude fosse buffer (ResponseContentRead).
        Assert.Equal(0, content.SerializationCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../etc/passwd")]
    [InlineData("/leading-segment")]
    [InlineData("trailing-segment/")]
    [InlineData("back\\slash")]
    public async Task Open_read_validates_the_object_key(string providerKey)
    {
        using var handler = new RequestSequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = CreateStorage(client);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(providerKey, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Open_read_fails_closed_on_http_error(HttpStatusCode statusCode)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("storage error", Encoding.UTF8, "text/plain")
        };
        using var handler = new RequestSequenceHandler(_ => response);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://storage.example.test/") };
        var storage = CreateStorage(client);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => storage.OpenReadAsync("quarantine/company/document", CancellationToken.None));
        // Fail-closed devolve a conexão: a response de erro foi disposta pelo storage.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => response.Content.ReadAsStreamAsync());
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static SupabaseFileStorage CreateStorage(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-credential");
        client.DefaultRequestHeaders.Add("apikey", "not-a-credential");
        return new SupabaseFileStorage(client);
    }

    private static string ObjectPage(int start, int count) => JsonSerializer.Serialize(
        Enumerable.Range(start, count)
            .Select(index => new { name = $"quarantine/company/{index:D4}", created_at = "2026-08-10T00:00:00Z" }));

    private static string ReadBody(HttpRequestMessage request) => request.Content!
        .ReadAsStringAsync()
        .GetAwaiter()
        .GetResult();

    private sealed class SequencedResponseHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _responseIndex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_responseIndex >= responses.Length)
            {
                throw new InvalidOperationException("No test response is available.");
            }

            return Task.FromResult(responses[_responseIndex++]);
        }
    }

    private sealed class RequestSequenceHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int responseIndex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (responseIndex >= responses.Length)
            {
                throw new InvalidOperationException("No request response is available.");
            }

            return Task.FromResult(responses[responseIndex++](request));
        }
    }

    /// <summary>
    /// Conteúdo de resposta que conta materializações: SerializeToStreamAsync só é chamado
    /// pelo HttpClient quando a completude é buffer (ResponseContentRead), nunca com
    /// ResponseHeadersRead; ReadAsStreamAsync devolve o stream direto.
    /// </summary>
    private sealed class NonBufferedContent(byte[] payload) : HttpContent
    {
        private int serializationCount;

        public int SerializationCount => serializationCount;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            serializationCount++;
            await stream.WriteAsync(payload);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(payload, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }
    }
}
