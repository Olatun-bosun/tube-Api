
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YouTube.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class YouTubeController : ControllerBase
    {
        private readonly ILogger<YouTubeController> _logger;
        private readonly string _downloadPath;
        private readonly string _ytDlpPath;
        private readonly string? _ffmpegPath;
        
        // Track active downloads
        private static readonly ConcurrentDictionary<string, DownloadSession> _activeDownloads = new();

        public YouTubeController(ILogger<YouTubeController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _downloadPath = configuration["DownloadPath"] ?? Path.Combine(Path.GetTempPath(), "youtube-downloads");
            _ytDlpPath = configuration["YtDlpPath"] ?? "yt-dlp";
            _ffmpegPath = configuration["FFmpegPath"];

            Directory.CreateDirectory(_downloadPath);
        }

        // APPROACH 1: Start download with progress tracking
        [HttpPost("start-download")]
        public async Task<IActionResult> StartDownload([FromBody] DownloadRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.VideoUrl) || !IsValidYouTubeUrl(request.VideoUrl))
                {
                    return BadRequest("Invalid YouTube URL");
                }

                var sessionId = Guid.NewGuid().ToString("N")[..8];
                var outputTemplate = Path.Combine(_downloadPath, $"{sessionId}_%(title)s.%(ext)s");

                var downloadSession = new DownloadSession
                {
                    Id = sessionId,
                    VideoUrl = request.VideoUrl,
                    Quality = request.Quality ?? "best",
                    Status = DownloadStatus.Starting,
                    StartTime = DateTime.UtcNow
                };

                _activeDownloads[sessionId] = downloadSession;

                // Start download in background
                _ = Task.Run(async () => await ProcessDownloadAsync(sessionId, request, outputTemplate));

                return Ok(new { sessionId, status = "started" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting download: {Url}", request.VideoUrl);
                return StatusCode(500, $"Error starting download: {ex.Message}");
            }
        }

        [HttpPost("download")]
        public async Task<IActionResult> DownloadVideo([FromQuery] DownloadRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.VideoUrl))
                {
                    return BadRequest("Video URL is required");
                }

                if (!IsValidYouTubeUrl(request.VideoUrl))
                {
                    return BadRequest("Invalid YouTube URL");
                }

                // Generate unique filename to avoid conflicts
                var sessionId = Guid.NewGuid().ToString("N")[..8];
                var outputTemplate = Path.Combine(_downloadPath, $"{sessionId}_%(title)s.%(ext)s");

                // Build yt-dlp command arguments for best quality
                var arguments = new List<string>();

                // Add FFmpeg path if configured (must be first)
                if (!string.IsNullOrEmpty(_ffmpegPath))
                {
                    arguments.Add("--ffmpeg-location");
                    arguments.Add($"\"{_ffmpegPath}\"");
                }

                arguments.AddRange(new[]
                {
                            "-f", GetQualityFormat(request.Quality),
                            "-o", $"\"{outputTemplate}\"",
                            "--no-playlist",
                            "--restrict-filenames",
                            "--merge-output-format", "mp4",
                            "--embed-metadata",
                            request.VideoUrl
                        });

                var result = await RunYtDlpAsync(arguments);

                if (!result.Success)
                {
                    return BadRequest($"Download failed: {result.Error}");
                }

                // Find the downloaded file
                var downloadedFiles = Directory.GetFiles(_downloadPath, $"{sessionId}_*");
                if (downloadedFiles.Length == 0)
                {
                    return NotFound("Downloaded file not found");
                }

                var filePath = downloadedFiles[0];
                var fileName = Path.GetFileName(filePath).Substring(9); // Remove session prefix
                var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);

                // Clean up the file after reading
                System.IO.File.Delete(filePath);

                var contentType = GetContentType(Path.GetExtension(filePath));
                return File(fileBytes, contentType, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading video: {Url}", request.VideoUrl);
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }


        // Get download progress
        [HttpGet("progress/{sessionId}")]
        public IActionResult GetProgress(string sessionId)
        {
            if (!_activeDownloads.TryGetValue(sessionId, out var session))
            {
                return NotFound("Download session not found");
            }

            return Ok(new
            {
                sessionId,
                status = session.Status.ToString().ToLower(),
                progress = session.Progress,
                speed = session.Speed,
                eta = session.ETA,
                fileName = session.FileName,
                fileSize = session.FileSize,
                downloadedSize = session.DownloadedSize,
                error = session.Error,
                startTime = session.StartTime,
                completed = session.Status == DownloadStatus.Completed
            });
        }

        // Cancel download
        [HttpPost("cancel/{sessionId}")]
        public IActionResult CancelDownload(string sessionId)
        {
            if (!_activeDownloads.TryGetValue(sessionId, out var session))
            {
                return NotFound("Download session not found");
            }

            session.CancellationTokenSource.Cancel();
            session.Status = DownloadStatus.Cancelled;

            return Ok(new { sessionId, status = "cancelled" });
        }

        // Download completed file
        [HttpGet("file/{sessionId}")]
        public async Task<IActionResult> DownloadFile(string sessionId)
        {
            if (!_activeDownloads.TryGetValue(sessionId, out var session))
            {
                return NotFound("Download session not found");
            }

            if (session.Status != DownloadStatus.Completed || string.IsNullOrEmpty(session.FilePath))
            {
                return BadRequest("Download not completed or file not available");
            }

            if (!System.IO.File.Exists(session.FilePath))
            {
                return NotFound("File not found on disk");
            }

            var fileName = Path.GetFileName(session.FilePath);
            if (fileName.Length > 9) fileName = fileName.Substring(9); // Remove session prefix

            var fileBytes = await System.IO.File.ReadAllBytesAsync(session.FilePath);
            var contentType = GetContentType(Path.GetExtension(session.FilePath));

            // Optionally delete after serving
            // System.IO.File.Delete(session.FilePath);
            // _activeDownloads.TryRemove(sessionId, out _);

            return File(fileBytes, contentType, fileName);
        }

        //// Replace your stream-download method with this implementation
        //[HttpGet("stream-download")]
        //public async Task<IActionResult> StreamDownload([FromQuery] DownloadRequest request)
        //{
        //    try
        //    {
        //        if (string.IsNullOrEmpty(request.VideoUrl) || !IsValidYouTubeUrl(request.VideoUrl))
        //        {
        //            return BadRequest("Invalid YouTube URL");
        //        }

        //        // First get video info for proper filename
        //        var videoInfoObj = await GetVideoInfoInternal(request.VideoUrl);
        //        var fileName = "video.mp4"; // Default filename

        //        if (videoInfoObj != null)
        //        {
        //            var videoInfoJson = JsonSerializer.Serialize(videoInfoObj);
        //            var videoInfoElement = JsonSerializer.Deserialize<JsonElement>(videoInfoJson);

        //            if (videoInfoElement.TryGetProperty("Title", out var titleProp))
        //            {
        //                var title = titleProp.GetString();
        //                if (!string.IsNullOrEmpty(title))
        //                {
        //                    // Sanitize filename for download
        //                    var sanitizedTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
        //                    fileName = $"{sanitizedTitle}.mp4";
        //                }
        //            }
        //        }

        //        // Use yt-dlp to stream directly to response
        //        var arguments = new List<string>
        //{
        //    "-f", GetQualityFormat(request.Quality),
        //    "--no-playlist",
        //    "-o", "-", // Output to stdout
        //    request.VideoUrl
        //};

        //        return await StreamYtDlpToResponse(arguments, fileName);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error streaming download: {Url}", request.VideoUrl);
        //        return StatusCode(500, $"Error streaming download: {ex.Message}");
        //    }
        //}

        // New method to stream yt-dlp output directly to HTTP response
        private async Task<IActionResult> StreamYtDlpToResponse(List<string> arguments, string fileName)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = string.Join(" ", arguments),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();

                // Set response headers for download
                Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                Response.ContentType = "application/octet-stream";

                // Stream the output directly to response
                await process.StandardOutput.BaseStream.CopyToAsync(Response.Body);

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogError("yt-dlp failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                    return StatusCode(500, "Download failed");
                }

                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during streaming download");
                if (!process.HasExited)
                {
                    process.Kill();
                }
                throw;
            }
            finally
            {
                process?.Dispose();
            }
        }
        // APPROACH 1: Return the direct download URL for browser to handle
        [HttpGet("get-download-url")]
        public async Task<IActionResult> GetDownloadUrl([FromQuery] DownloadRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.VideoUrl) || !IsValidYouTubeUrl(request.VideoUrl))
                {
                    return BadRequest("Invalid YouTube URL");
                }

                // Get direct URL from yt-dlp
                var arguments = new List<string>
        {
            "-f", GetQualityFormat(request.Quality),
            "--get-url",
            "--get-filename",
            "--no-playlist",
            request.VideoUrl
        };

                var result = await RunYtDlpAsync(arguments);
                if (!result.Success)
                {
                    return BadRequest($"Failed to get download URL: {result.Error}");
                }

                var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2)
                {
                    return BadRequest("Could not get download URL and filename");
                }

                var directUrl = lines[0].Trim();
                var filename = lines[1].Trim();

                if (string.IsNullOrEmpty(directUrl))
                {
                    return BadRequest("Could not get direct download URL");
                }

                // Also get video info for additional metadata
                var videoInfo = await GetVideoInfoInternal(request.VideoUrl);

                return Ok(new
                {
                    downloadUrl = directUrl,
                    filename = filename,
                    videoInfo = videoInfo,
                    message = "Use this URL to download directly in your browser",
                    instructions = "Copy the downloadUrl and paste it in a new browser tab, or use it in a download manager"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting download URL: {Url}", request.VideoUrl);
                return StatusCode(500, $"Error getting download URL: {ex.Message}");
            }
        }

        //// APPROACH 2: Endpoint that triggers immediate download in browser
        //[HttpGet("direct-download")]
        //public async Task<IActionResult> DirectDownload([FromQuery] DownloadRequest request)
        //{
        //    try
        //    {
        //        if (string.IsNullOrEmpty(request.VideoUrl) || !IsValidYouTubeUrl(request.VideoUrl))
        //        {
        //            return BadRequest("Invalid YouTube URL");
        //        }

        //        // Get video info first for proper filename
        //        var videoInfoObj = await GetVideoInfoInternal(request.VideoUrl);
        //        var fileName = "video.mp4";

        //        if (videoInfoObj != null)
        //        {
        //            var videoInfoJson = JsonSerializer.Serialize(videoInfoObj);
        //            var videoInfoElement = JsonSerializer.Deserialize<JsonElement>(videoInfoJson);

        //            if (videoInfoElement.TryGetProperty("Title", out var titleProp))
        //            {
        //                var title = titleProp.GetString();
        //                if (!string.IsNullOrEmpty(title))
        //                {
        //                    // Sanitize filename
        //                    var sanitizedTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
        //                    fileName = $"{sanitizedTitle}.mp4";
        //                }
        //            }
        //        }

        //        // Get the direct URL
        //        var urlArguments = new List<string>
        //{
        //    "-f", GetQualityFormat(request.Quality),
        //    "--get-url",
        //    "--no-playlist",
        //    request.VideoUrl
        //};

        //        var urlResult = await RunYtDlpAsync(urlArguments);
        //        if (!urlResult.Success)
        //        {
        //            return BadRequest($"Failed to get download URL: {urlResult.Error}");
        //        }

        //        var directUrl = urlResult.Output.Trim();
        //        if (string.IsNullOrEmpty(directUrl))
        //        {
        //            return BadRequest("Could not get direct download URL");
        //        }

        //        // Return a redirect response that will trigger browser download
        //        // Set headers to force download
        //        Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");

        //        // Redirect to the direct URL - browser will download it
        //        return Redirect(directUrl);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error in direct download: {Url}", request.VideoUrl);
        //        return StatusCode(500, $"Error in direct download: {ex.Message}");
        //    }
        //}

        //// BONUS: Hybrid approach - return URL with download instructions
        //[HttpGet("download-info")]
        //public async Task<IActionResult> GetDownloadInfo([FromQuery] DownloadRequest request)
        //{
        //    try
        //    {
        //        if (string.IsNullOrEmpty(request.VideoUrl) || !IsValidYouTubeUrl(request.VideoUrl))
        //        {
        //            return BadRequest("Invalid YouTube URL");
        //        }

        //        // Get both URL and video info
        //        var urlArguments = new List<string>
        //{
        //    "-f", GetQualityFormat(request.Quality),
        //    "--get-url",
        //    "--get-filename",
        //    "--no-playlist",
        //    request.VideoUrl
        //};

        //        var result = await RunYtDlpAsync(arguments: urlArguments);
        //        if (!result.Success)
        //        {
        //            return BadRequest($"Failed to get download info: {result.Error}");
        //        }

        //        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        //        var directUrl = lines.Length > 0 ? lines[0].Trim() : "";
        //        var filename = lines.Length > 1 ? lines[1].Trim() : "video.mp4";

        //        var videoInfo = await GetVideoInfoInternal(request.VideoUrl);

        //        return Ok(new
        //        {
        //            directUrl = directUrl,
        //            filename = filename,
        //            videoInfo = videoInfo,
        //            downloadMethods = new
        //            {
        //                browser = $"Open this URL in a new tab: {directUrl}",
        //                api = $"GET {Request.Scheme}://{Request.Host}/api/YouTube/direct-download?videoUrl={Uri.EscapeDataString(request.VideoUrl)}&quality={request.Quality}",
        //                curl = $"curl -L \"{directUrl}\" -o \"{filename}\""
        //            }
        //        });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error getting download info: {Url}", request.VideoUrl);
        //        return StatusCode(500, $"Error getting download info: {ex.Message}");
        //    }
        //}
        //// 2. Add missing endpoint for WebSocket support
        //[HttpPost("start-ws-download")]
        //public async Task<IActionResult> StartWebSocketDownload([FromBody] DownloadRequest request)
        //{
        //    try
        //    {
        //        if (string.IsNullOrEmpty(request.VideoUrl) || !IsValidYouTubeUrl(request.VideoUrl))
        //        {
        //            return BadRequest("Invalid YouTube URL");
        //        }

        //        var sessionId = Guid.NewGuid().ToString("N")[..8];
        //        var outputTemplate = Path.Combine(_downloadPath, $"{sessionId}_%(title)s.%(ext)s");

        //        var downloadSession = new DownloadSession
        //        {
        //            Id = sessionId,
        //            VideoUrl = request.VideoUrl,
        //            Quality = request.Quality ?? "best",
        //            Status = DownloadStatus.Starting,
        //            StartTime = DateTime.UtcNow
        //        };

        //        _activeDownloads[sessionId] = downloadSession;

        //        // Start download in background
        //        _ = Task.Run(async () => await ProcessDownloadAsync(sessionId, request, outputTemplate));

        //        return Ok(new { sessionId, status = "started", websocketUrl = $"/api/YouTube/ws-download/{sessionId}" });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error starting WebSocket download: {Url}", request.VideoUrl);
        //        return StatusCode(500, $"Error starting WebSocket download: {ex.Message}");
        //    }
        //}

        // 3. Add cleanup endpoint for old downloads
        [HttpDelete("cleanup/{sessionId}")]
        public IActionResult CleanupSession(string sessionId)
        {
            if (_activeDownloads.TryRemove(sessionId, out var session))
            {
                // Cancel if still running
                if (!session.CancellationTokenSource.Token.IsCancellationRequested)
                {
                    session.CancellationTokenSource.Cancel();
                }

                // Delete file if exists
                if (!string.IsNullOrEmpty(session.FilePath) && System.IO.File.Exists(session.FilePath))
                {
                    try
                    {
                        System.IO.File.Delete(session.FilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete file: {FilePath}", session.FilePath);
                    }
                }

                return Ok(new { sessionId, status = "cleaned" });
            }

            return NotFound("Session not found");
        }

        // 4. Add endpoint to list active downloads
        [HttpGet("active-downloads")]
        public IActionResult GetActiveDownloads()
        {
            var activeDownloads = _activeDownloads.Values.Select(session => new
            {
                sessionId = session.Id,
                videoUrl = session.VideoUrl,
                status = session.Status.ToString().ToLower(),
                progress = session.Progress,
                startTime = session.StartTime,
                fileName = session.FileName
            }).ToList();

            return Ok(activeDownloads);
        }

        //// APPROACH 3: WebSocket for real-time progress
        //[HttpGet("ws-download/{sessionId}")]
        //public async Task<IActionResult> WebSocketDownload(string sessionId)
        //{
        //    if (!HttpContext.WebSockets.IsWebSocketRequest)
        //    {
        //        return BadRequest("WebSocket connection required");
        //    }

        //    var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            
        //    if (_activeDownloads.TryGetValue(sessionId, out var session))
        //    {
        //        session.WebSocket = webSocket;
                
        //        // Keep connection alive and send updates
        //        var buffer = new byte[1024 * 4];
        //        while (webSocket.State == System.Net.WebSockets.WebSocketState.Open && 
        //               session.Status != DownloadStatus.Completed)
        //        {
        //            await Task.Delay(1000); // Send updates every second
                    
        //            var progressUpdate = JsonSerializer.Serialize(new
        //            {
        //                progress = session.Progress,
        //                speed = session.Speed,
        //                eta = session.ETA,
        //                status = session.Status.ToString()
        //            });
                    
        //            var bytes = System.Text.Encoding.UTF8.GetBytes(progressUpdate);
        //            await webSocket.SendAsync(
        //                new ArraySegment<byte>(bytes), 
        //                System.Net.WebSockets.WebSocketMessageType.Text, 
        //                true, 
        //                CancellationToken.None);
        //        }
        //    }

        //    return new EmptyResult();
        //}

        // Existing methods...
        [HttpGet("info")]
        public async Task<IActionResult> GetVideoInfo([FromQuery] string videoUrl)
        {
            try
            {
                var info = await GetVideoInfoInternal(videoUrl);
                if (info == null)
                {
                    return BadRequest("Failed to get video info");
                }

                return Ok(info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting video info: {Url}", videoUrl);
                return StatusCode(500, $"Error getting video info: {ex.Message}");
            }
        }

        // Background download processing with progress tracking
        private async Task ProcessDownloadAsync(string sessionId, DownloadRequest request, string outputTemplate)
        {
            var session = _activeDownloads[sessionId];
            
            try
            {
                session.Status = DownloadStatus.InProgress;

                var arguments = new List<string>();

                if (!string.IsNullOrEmpty(_ffmpegPath))
                {
                    arguments.Add("--ffmpeg-location");
                    arguments.Add($"\"{_ffmpegPath}\"");
                }

                arguments.AddRange(new[]
                {
                    "-f", GetQualityFormat(request.Quality),
                    "-o", $"\"{outputTemplate}\"",
                    "--no-playlist",
                    "--restrict-filenames",
                    "--merge-output-format", "mp4",
                    "--embed-metadata",
                    "--newline", // Each progress update on new line
                    request.VideoUrl
                });

                await RunYtDlpWithProgressAsync(arguments, session);

                if (session.Status != DownloadStatus.Cancelled)
                {
                    // Find downloaded file
                    var downloadedFiles = Directory.GetFiles(_downloadPath, $"{sessionId}_*");
                    if (downloadedFiles.Length > 0)
                    {
                        session.FilePath = downloadedFiles[0];
                        session.FileName = Path.GetFileName(downloadedFiles[0]);
                        session.Status = DownloadStatus.Completed;
                    }
                    else
                    {
                        session.Status = DownloadStatus.Failed;
                        session.Error = "Downloaded file not found";
                    }
                }
            }
            catch (Exception ex)
            {
                session.Status = DownloadStatus.Failed;
                session.Error = ex.Message;
                _logger.LogError(ex, "Download failed for session {SessionId}", sessionId);
            }
        }

        private async Task<YtDlpResult> RunYtDlpWithProgressAsync(List<string> arguments, DownloadSession session)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = string.Join(" ", arguments),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) => {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    ParseProgressUpdate(e.Data, session);
                }
            };

            process.ErrorDataReceived += (sender, e) => {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for completion or cancellation
            while (!process.HasExited && !session.CancellationTokenSource.Token.IsCancellationRequested)
            {
                await Task.Delay(100);
            }

            if (session.CancellationTokenSource.Token.IsCancellationRequested && !process.HasExited)
            {
                process.Kill();
            }

            var success = process.ExitCode == 0;
            return new YtDlpResult
            {
                Success = success,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                ExitCode = process.ExitCode
            };
        }

        private void ParseProgressUpdate(string output, DownloadSession session)
        {
            // Parse yt-dlp progress output
            // Format: [download]  45.2% of 125.45MiB at 2.35MiB/s ETA 00:32
            var progressRegex = new Regex(@"\[download\]\s+(\d+\.?\d*)%.*?(\d+\.?\d*\w+iB)\s+at\s+(\d+\.?\d*\w+iB/s).*?ETA\s+(\d{2}:\d{2})");
            var match = progressRegex.Match(output);

            if (match.Success)
            {
                if (double.TryParse(match.Groups[1].Value, out var progress))
                {
                    session.Progress = progress;
                }
                session.FileSize = match.Groups[2].Value;
                session.Speed = match.Groups[3].Value;
                session.ETA = match.Groups[4].Value;

                // Notify WebSocket if connected
                if (session.WebSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var update = JsonSerializer.Serialize(new
                    {
                        progress = session.Progress,
                        speed = session.Speed,
                        eta = session.ETA
                    });
                    
                    var bytes = System.Text.Encoding.UTF8.GetBytes(update);
                    _ = session.WebSocket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        System.Net.WebSockets.WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
        }

        private async Task<object?> GetVideoInfoInternal(string videoUrl)
        {
            if (string.IsNullOrEmpty(videoUrl) || !IsValidYouTubeUrl(videoUrl))
                return null;

            var arguments = new List<string>
            {
                "--dump-json",
                "--no-playlist",
                videoUrl
            };

            var result = await RunYtDlpAsync(arguments);
            if (!result.Success) return null;

            var videoInfo = JsonSerializer.Deserialize<JsonElement>(result.Output);

            return new
            {
                Title = GetJsonString(videoInfo, "title"),
                Author = GetJsonString(videoInfo, "uploader"),
                Duration = GetJsonInt(videoInfo, "duration"),
                DurationString = TimeSpan.FromSeconds(GetJsonInt(videoInfo, "duration")).ToString(@"mm\:ss"),
                Description = GetJsonString(videoInfo, "description"),
                ThumbnailUrl = GetJsonString(videoInfo, "thumbnail"),
                UploadDate = GetJsonString(videoInfo, "upload_date"),
                ViewCount = GetJsonLong(videoInfo, "view_count"),
                VideoId = GetJsonString(videoInfo, "id"),
                AvailableFormats = GetAvailableFormats(videoInfo)
            };
        }

        // Keep existing helper methods...
        private async Task<YtDlpResult> RunYtDlpAsync(List<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = string.Join(" ", arguments),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) => {
                if (e.Data != null) outputBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (sender, e) => {
                if (e.Data != null) errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            return new YtDlpResult
            {
                Success = process.ExitCode == 0,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                ExitCode = process.ExitCode
            };
        }

        private static string GetQualityFormat(string? quality)
        {
            // Fix: Handle null/empty quality properly
            if (string.IsNullOrEmpty(quality))
            {
                quality = "best";
            }

            return quality.ToLower() switch
            {
                "best" => "best[ext=mp4]/best",
                "best-merge" => "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio",
                "4k" => "best[height<=2160][ext=mp4]/best[height<=2160]",
                "1440p" => "best[height<=1440][ext=mp4]/best[height<=1440]",
                "1080p" => "best[height<=1080][ext=mp4]/best[height<=1080]",
                "720p" => "best[height<=720][ext=mp4]/best[height<=720]",
                "480p" => "best[height<=480][ext=mp4]/best[height<=480]",
                "360p" => "best[height<=360][ext=mp4]/best[height<=360]",
                "worst" => "worst[ext=mp4]/worst",
                _ => "best[ext=mp4]/best"
            };
        }

        private static bool IsValidYouTubeUrl(string url)
        {
            var patterns = new[]
            {
                @"^https?://(www\.)?youtube\.com/watch\?v=[\w-]+",
                @"^https?://(www\.)?youtu\.be/[\w-]+",
                @"^https?://(www\.)?youtube\.com/embed/[\w-]+",
                @"^https?://(www\.)?youtube\.com/v/[\w-]+"
            };

            return patterns.Any(pattern => Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase));
        }

        private static string GetContentType(string extension)
        {
            return extension.ToLower() switch
            {
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".mkv" => "video/x-matroska",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".wav" => "audio/wav",
                _ => "application/octet-stream"
            };
        }

        private static string GetJsonString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() ?? ""
                : "";
        }

        private static int GetJsonInt(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetInt32()
                : 0;
        }

        private static long GetJsonLong(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetInt64()
                : 0;
        }

        private static List<object> GetAvailableFormats(JsonElement videoInfo)
        {
            var formats = new List<object>();

            if (videoInfo.TryGetProperty("formats", out var formatsArray) &&
                formatsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var format in formatsArray.EnumerateArray())
                {
                    if (format.TryGetProperty("vcodec", out var vcodec) &&
                        vcodec.GetString() != "none" &&
                        format.TryGetProperty("acodec", out var acodec) &&
                        acodec.GetString() != "none")
                    {
                        formats.Add(new
                        {
                            Quality = GetJsonString(format, "format_note"),
                            Resolution = GetJsonString(format, "resolution"),
                            Extension = GetJsonString(format, "ext"),
                            FileSize = GetJsonLong(format, "filesize")
                        });
                    }
                }
            }

            return formats.Take(10).ToList();
        }
    }

    // Supporting classes
    public class DownloadRequest
    {
        public string VideoUrl { get; set; } = string.Empty;
        public string? Quality { get; set; } = "best";
    }

    public class YtDlpResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public int ExitCode { get; set; }
    }

    public class DownloadSession
    {
        public string Id { get; set; } = string.Empty;
        public string VideoUrl { get; set; } = string.Empty;
        public string Quality { get; set; } = string.Empty;
        public DownloadStatus Status { get; set; }
        public double Progress { get; set; }
        public string Speed { get; set; } = string.Empty;
        public string ETA { get; set; } = string.Empty;
        public string FileSize { get; set; } = string.Empty;
        public long DownloadedSize { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; } = new();
        public System.Net.WebSockets.WebSocket? WebSocket { get; set; }
    }

    public enum DownloadStatus
    {
        Starting,
        InProgress,
        Completed,
        Failed,
        Cancelled
    }

    public class VideoInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public int Duration { get; set; }
        public string DurationString { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public string UploadDate { get; set; } = string.Empty;
        public long ViewCount { get; set; }
        public string VideoId { get; set; } = string.Empty;
        public List<object> AvailableFormats { get; set; } = new();
    }
}