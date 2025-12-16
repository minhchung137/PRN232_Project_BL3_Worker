using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PRN232_GradingSystem_Worker_Services.Interfaces;

namespace PRN232_GradingSystem_Worker_Services.Implementations
{
    public sealed class BackblazeNativeDownloadService : IFileDownloadService
    {
        private readonly HttpClient _httpClient;
        private readonly BackblazeConfig _config;

        public BackblazeNativeDownloadService(HttpClient httpClient, BackblazeConfig config)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task<string> DownloadSubmissionZipAsync(string fileUrl, string targetPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(fileUrl))
                throw new ArgumentException("FileUrl is required", nameof(fileUrl));
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("TargetPath is required", nameof(targetPath));

            try
            {
                // 1. Authorize with Backblaze B2 API
                var authResponse = await AuthorizeAsync(cancellationToken);
                
                // 2. Get file info by name (extract fileName from fileUrl)
                var fileInfo = await GetFileInfoAsync(authResponse, fileUrl, cancellationToken);
                
                // 3. Download file
                var downloadedPath = await DownloadFileAsync(authResponse, fileInfo, targetPath, cancellationToken);
                
                return downloadedPath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Download failed for file {fileUrl}: {ex.Message}", ex);
            }
        }

        private async Task<B2AuthResponse> AuthorizeAsync(CancellationToken cancellationToken)
        {
            var authUrl = $"{_config.ApiEndpoint}/b2api/v2/b2_authorize_account";
            
            // Backblaze B2 uses GET request with Basic Auth, not POST with JSON
            var request = new HttpRequestMessage(HttpMethod.Get, authUrl);
            var credentials = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_config.KeyId}:{_config.ApplicationKey}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            // Debug: Log response details
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Backblaze B2 authorization failed. Status: {response.StatusCode}, Response: {responseContent}");
            }

            var authResponse = JsonSerializer.Deserialize<B2AuthResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (authResponse == null)
                throw new InvalidOperationException("Failed to authorize with Backblaze B2");

            return authResponse;
        }

        private async Task<B2FileInfo> GetFileInfoAsync(B2AuthResponse authResponse, string fileUrl, CancellationToken cancellationToken)
        {
            // Extract fileName from fileUrl: BucketName/fileName -> fileName
            var fileName = fileUrl;
            if (fileUrl.StartsWith($"{_config.BucketName}/", StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileUrl.Substring(_config.BucketName.Length + 1);
            }

            var listUrl = $"{authResponse.ApiUrl}/b2api/v2/b2_list_file_names";
            
            var listRequest = new
            {
                bucketId = "b6d1fc7153a6206d9dab0612",
                //bucketId = authResponse.Allowed.BucketId,
                startFileName = fileName,
                maxFileCount = 1
            };

            var json = JsonSerializer.Serialize(listRequest);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, listUrl)
            {
                Content = content
            };
            request.Headers.TryAddWithoutValidation("Authorization", authResponse.AuthorizationToken);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to list files. Status: {response.StatusCode}, Response: {responseContent}");
            }

            var listResponse = JsonSerializer.Deserialize<B2ListFilesResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (listResponse?.Files == null || listResponse.Files.Count == 0)
                throw new FileNotFoundException($"File not found: {fileUrl}");

            var file = listResponse.Files[0];
            if (file.FileName != fileName)
                throw new FileNotFoundException($"File not found: {fileUrl} (expected {fileName}, got {file.FileName})");

            return file;
        }

        private async Task<string> DownloadFileAsync(B2AuthResponse authResponse, B2FileInfo fileInfo, string targetPath, CancellationToken cancellationToken)
        {
            // URL encode the fileName to handle special characters
            var encodedFileName = Uri.EscapeDataString(fileInfo.FileName);
            var downloadUrl = $"{authResponse.DownloadUrl}/file/{_config.BucketName}/{encodedFileName}";
            
            var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.TryAddWithoutValidation("Authorization", authResponse.AuthorizationToken);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Failed to download file. URL: {downloadUrl}, Status: {response.StatusCode}, Response: {errorContent}");
            }

            // Ensure target directory exists
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // Download to file
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
            await response.Content.CopyToAsync(fileStream, cancellationToken);

            return targetPath;
        }

        // B2 API Response Models
        private sealed class B2AuthResponse
        {
            public string AccountId { get; set; } = string.Empty;
            public string ApiUrl { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public string AuthorizationToken { get; set; } = string.Empty;
            public B2Allowed Allowed { get; set; } = new();
        }

        private sealed class B2Allowed
        {
            public string BucketId { get; set; } = string.Empty;
        }

        private sealed class B2ListFilesResponse
        {
            public List<B2FileInfo> Files { get; set; } = new();
        }

        private sealed class B2FileInfo
        {
            public string FileId { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public long ContentLength { get; set; }
            public string ContentType { get; set; } = string.Empty;
            public string ContentSha1 { get; set; } = string.Empty;
        }
    }

    public sealed class BackblazeConfig
    {
        public string Mode { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public string BucketId { get; set; } = string.Empty;
        public string ApplicationKey { get; set; } = string.Empty;
        public string BucketName { get; set; } = string.Empty;
        public string ApiEndpoint { get; set; } = string.Empty;
        public string S3Endpoint { get; set; } = string.Empty;
    }
}
