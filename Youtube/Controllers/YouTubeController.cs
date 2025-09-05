
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

                var result = await RunYtDlpWithCookiesAsync(arguments);

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

            try
            {
                var fileName = Path.GetFileName(session.FilePath);
                if (fileName.Length > 9) fileName = fileName.Substring(9); // Remove session prefix

                var contentType = GetContentType(Path.GetExtension(session.FilePath));
                var fileInfo = new FileInfo(session.FilePath);

                // Fix: Use indexer instead of Add method to avoid duplicate key errors
                Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
                Response.Headers["Content-Length"] = fileInfo.Length.ToString();
                Response.ContentType = contentType;

                // Stream file directly to response
                using var fileStream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read);
                await fileStream.CopyToAsync(Response.Body);

                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming file for session {SessionId}", sessionId);
                return StatusCode(500, "Error streaming file");
            }
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

        // Fix 7: Also fix the other header warning
        private async Task<IActionResult> StreamYtDlpToResponse(List<string> arguments, string fileName)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Use ArgumentList
            startInfo.ArgumentList.Clear();
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();

                // Fix: Use indexer instead of Add
                Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
                Response.ContentType = "application/octet-stream";

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

                var result = await RunYtDlpWithCookiesAsync(arguments);
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
        // Add this new endpoint to test what formats are actually available
        [HttpGet("debug-formats")]
        public async Task<IActionResult> DebugAvailableFormats([FromQuery] string videoUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(videoUrl) || !IsValidYouTubeUrl(videoUrl))
                {
                    return BadRequest("Invalid YouTube URL");
                }

                // Get all available formats with detailed info
                var arguments = new List<string>
        {
            "-F", // List all available formats
            "--no-playlist",
            videoUrl
        };

                var result = await RunYtDlpWithCookiesAsync(arguments);
                if (!result.Success)
                {
                    return BadRequest($"Failed to get formats: {result.Error}");
                }

                return Ok(new
                {
                    videoUrl = videoUrl,
                    availableFormats = result.Output,
                    debugInfo = "Use this to see what formats are actually available for this video"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting debug formats for: {Url}", videoUrl);
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        // Enhanced test method to validate format selection and file sizes
        [HttpGet("test-quality")]
        public async Task<IActionResult> TestQualitySelection([FromQuery] string videoUrl, [FromQuery] string quality = "720p")
        {
            try
            {
                if (string.IsNullOrEmpty(videoUrl) || !IsValidYouTubeUrl(videoUrl))
                {
                    return BadRequest("Invalid YouTube URL");
                }

                var formatString = GetQualityFormat(quality);

                // Test what format would be selected with file size info
                var arguments = new List<string>
        {
            "-f", formatString,
            "--dump-json",
            "--no-playlist",
            videoUrl
        };

                var result = await RunYtDlpWithCookiesAsync(arguments);

                object? formatDetails = null;
                if (result.Success)
                {
                    try
                    {
                        var json = JsonSerializer.Deserialize<JsonElement>(result.Output);

                        // Handle both single format and merged format responses
                        if (json.ValueKind == JsonValueKind.Array)
                        {
                            // Multiple formats (video + audio merge)
                            var formats = new List<object>();
                            long totalSize = 0;

                            foreach (var format in json.EnumerateArray())
                            {
                                var size = GetJsonLong(format, "filesize") + GetJsonLong(format, "filesize_approx");
                                totalSize += size;

                                formats.Add(new
                                {
                                    formatId = GetJsonString(format, "format_id"),
                                    resolution = GetJsonString(format, "resolution"),
                                    width = GetJsonInt(format, "width"),
                                    height = GetJsonInt(format, "height"),
                                    filesize = size,
                                    formatNote = GetJsonString(format, "format_note"),
                                    vcodec = GetJsonString(format, "vcodec"),
                                    acodec = GetJsonString(format, "acodec"),
                                    ext = GetJsonString(format, "ext")
                                });
                            }

                            formatDetails = new
                            {
                                type = "merged",
                                formats = formats,
                                totalEstimatedSize = totalSize,
                                totalEstimatedSizeMB = Math.Round(totalSize / (1024.0 * 1024.0), 2)
                            };
                        }
                        else
                        {
                            // Single format
                            var size = GetJsonLong(json, "filesize") + GetJsonLong(json, "filesize_approx");
                            formatDetails = new
                            {
                                type = "single",
                                formatId = GetJsonString(json, "format_id"),
                                resolution = GetJsonString(json, "resolution"),
                                width = GetJsonInt(json, "width"),
                                height = GetJsonInt(json, "height"),
                                filesize = size,
                                filesizeMB = Math.Round(size / (1024.0 * 1024.0), 2),
                                formatNote = GetJsonString(json, "format_note"),
                                vcodec = GetJsonString(json, "vcodec"),
                                acodec = GetJsonString(json, "acodec"),
                                ext = GetJsonString(json, "ext")
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse format details");
                        formatDetails = new { error = ex.Message, rawOutput = result.Output };
                    }
                }

                return Ok(new
                {
                    requestedQuality = quality,
                    formatString = formatString,
                    success = result.Success,
                    error = result.Error,
                    formatDetails = formatDetails,
                    instructions = new
                    {
                        message = "Check the filesizeMB or totalEstimatedSizeMB values",
                        note = "Different qualities should show different file sizes now"
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing quality selection: {Url}", videoUrl);
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        // Fix 1: Update ProcessDownloadAsync to handle Docker environment properly
        private async Task ProcessDownloadAsync(string sessionId, DownloadRequest request, string outputTemplate)
        {
            var session = _activeDownloads[sessionId];

            try
            {
                session.Status = DownloadStatus.InProgress;

                var arguments = new List<string>();

                // Fix: Find FFmpeg in Docker environment
                var possibleFFmpegPaths = new[]
                {
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/app/ffmpeg",
            "ffmpeg" // Let system find it
        };

                string? workingFFmpegPath = null;
                foreach (var path in possibleFFmpegPaths)
                {
                    if (path == "ffmpeg" || System.IO.File.Exists(path))
                    {
                        workingFFmpegPath = path;
                        break;
                    }
                }

                if (workingFFmpegPath != null)
                {
                    arguments.Add("--ffmpeg-location");
                    arguments.Add(workingFFmpegPath);
                    _logger.LogInformation("Using FFmpeg at: {FFmpegPath}", workingFFmpegPath);
                }
                else
                {
                    _logger.LogWarning("FFmpeg not found at any expected location, continuing without it");
                }

                var quality = request.Quality ?? "best";
                _logger.LogInformation("Using quality: {Quality}", quality);

                arguments.AddRange(new[]
                {
            "-f", GetQualityFormat(quality),
            "-o", outputTemplate,
            "--no-playlist",
            "--restrict-filenames",
            "--merge-output-format", "mp4",
            "--embed-metadata",
            "--newline",
            request.VideoUrl
        });

                _logger.LogInformation("yt-dlp command args: {Args}", string.Join(" ", arguments));

                await RunYtDlpWithProgressAsync(arguments, session);

                if (session.Status != DownloadStatus.Cancelled)
                {
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


        // Supporting class for cookie setup
        public class CookieSetupRequest
        {
            public string Browser { get; set; } = "chrome";
        }

        // Fix 2: Update the RunYtDlpWithProgressAsync method similarly
        private async Task<YtDlpResult> RunYtDlpWithProgressAsync(List<string> arguments, DownloadSession session)
        {
            // Apply the same cookie and anti-detection measures
            var enhancedArgs = new List<string>();

            enhancedArgs.Add("--user-agent");
            enhancedArgs.Add("\"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36\"");

            enhancedArgs.Add("--referer");
            enhancedArgs.Add("https://www.youtube.com/");

            enhancedArgs.Add("--cookies-from-browser");
            enhancedArgs.Add("chrome");

            enhancedArgs.AddRange(new[]
            {
        "--sleep-interval", "1",
        "--max-sleep-interval", "5",
        "--extractor-retries", "3",
        "--socket-timeout", "30"
    });

            enhancedArgs.AddRange(arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Use ArgumentList instead of Arguments string
            startInfo.ArgumentList.Clear();
            foreach (var arg in enhancedArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

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

        // Fix 4: Remove or modify cookie setup endpoint for Docker
        [HttpPost("setup-cookies")]
        public async Task<IActionResult> SetupCookies([FromBody] CookieSetupRequest request)
        {
            try
            {
                // In Docker, we can't access browser cookies, so we test without them
                var testArgs = new List<string>
        {
            "--dump-json",
            "--no-playlist",
            "--user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "--referer", "https://www.youtube.com/",
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
        };

                var result = await RunYtDlpTestAsync(testArgs);

                if (result.Success)
                {
                    return Ok(new
                    {
                        message = "YouTube API access test successful (Docker mode - no browser cookies)",
                        mode = "docker",
                        status = "ready",
                        note = "Running in Docker environment without browser cookie support"
                    });
                }
                else
                {
                    return BadRequest(new
                    {
                        message = "YouTube API access test failed",
                        error = result.Error,
                        mode = "docker",
                        suggestions = new[]
                        {
                    "YouTube may be rate limiting or blocking requests",
                    "Try using a VPN or different server location",
                    "Consider implementing proxy rotation",
                    "Try updating yt-dlp to the latest version"
                }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing YouTube access");
                return StatusCode(500, $"Error testing YouTube access: {ex.Message}");
            }
        }

        // Fix 5: Add Docker diagnostics endpoint
        [HttpGet("docker-diagnostics")]
        public async Task<IActionResult> DockerDiagnostics()
        {
            try
            {
                var diagnostics = new
                {
                    environment = "Docker",
                    downloadPath = _downloadPath,
                    downloadPathExists = Directory.Exists(_downloadPath),
                    ytDlpPath = _ytDlpPath,
                    ytDlpExists = CheckCommandExists(_ytDlpPath),
                    ffmpegPaths = new
                    {
                        configured = _ffmpegPath ?? "null",
                        usrBin = System.IO.File.Exists("/usr/bin/ffmpeg"),
                        usrLocalBin = System.IO.File.Exists("/usr/local/bin/ffmpeg"),
                        systemPath = CheckCommandExists("ffmpeg")
                    },
                    systemInfo = new
                    {
                        workingDirectory = Directory.GetCurrentDirectory(),
                        tempPath = Path.GetTempPath(),
                        containerUser = Environment.UserName,
                        environmentVariables = Environment.GetEnvironmentVariables()
                            .Cast<System.Collections.DictionaryEntry>()
                            .Where(e => e.Key.ToString()?.Contains("PATH") == true ||
                                       e.Key.ToString()?.Contains("HOME") == true ||
                                       e.Key.ToString()?.Contains("USER") == true)
                            .ToDictionary(e => e.Key.ToString()!, e => e.Value?.ToString())
                    },
                    ytDlpVersion = await GetYtDlpVersion()
                };

                return Ok(diagnostics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running Docker diagnostics");
                return StatusCode(500, $"Diagnostics error: {ex.Message}");
            }
        }

        // Helper methods
        private bool CheckCommandExists(string command)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> GetYtDlpVersion()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                var version = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                return process.ExitCode == 0 ? version.Trim() : "Unknown";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private async Task<YtDlpResult> RunYtDlpTestAsync(List<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Clear();
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

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

            // Add timeout for testing
            var timeoutTask = Task.Delay(30000); // 30 second timeout
            var processTask = process.WaitForExitAsync();

            var completedTask = await Task.WhenAny(processTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                process.Kill();
                return new YtDlpResult
                {
                    Success = false,
                    Error = "Cookie extraction test timed out",
                    ExitCode = -1
                };
            }

            return new YtDlpResult
            {
                Success = process.ExitCode == 0,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                ExitCode = process.ExitCode
            };
        }

        private void ParseProgressUpdate(string output, DownloadSession session)
        {
            try
            {
                // Handle different types of yt-dlp output

                // 1. Regular download progress: [download]  45.2% of 125.45MiB at 2.35MiB/s ETA 00:32
                var progressRegex = new Regex(@"\[download\]\s+(\d+\.?\d*)%.*?(\d+\.?\d*\w+iB)\s+at\s+(\d+\.?\d*\w+iB/s).*?ETA\s+(\d{2}:\d{2})");
                var progressMatch = progressRegex.Match(output);

                // 2. Multi-stream info: [info] f137: Downloading webpage  
                var infoRegex = new Regex(@"\[info\]\s+(.+)");
                var infoMatch = infoRegex.Match(output);

                // 3. Merging info: [ffmpeg] Merging formats into "filename.mp4"
                var mergeRegex = new Regex(@"\[ffmpeg\]\s+Merging formats into");
                var mergeMatch = mergeRegex.Match(output);

                if (progressMatch.Success)
                {
                    if (double.TryParse(progressMatch.Groups[1].Value, out var progress))
                    {
                        // Only update progress if it's higher than current (avoid regression during multi-stream)
                        if (progress > session.Progress || session.Progress == 0)
                        {
                            session.Progress = progress;
                        }
                    }

                    var currentFileSize = progressMatch.Groups[2].Value;
                    var currentSpeed = progressMatch.Groups[3].Value;
                    var currentETA = progressMatch.Groups[4].Value;

                    // Keep track of the largest file size seen (likely the video stream)
                    if (string.IsNullOrEmpty(session.FileSize) ||
                        IsLargerFileSize(currentFileSize, session.FileSize))
                    {
                        session.FileSize = currentFileSize;
                    }

                    session.Speed = currentSpeed;
                    session.ETA = currentETA;

                    _logger.LogDebug("Session {SessionId}: Progress {Progress}%, Size {FileSize}, Speed {Speed}",
                        session.Id, session.Progress, session.FileSize, session.Speed);
                }
                else if (mergeMatch.Success)
                {
                    // When merging starts, we're near completion
                    session.Progress = Math.Max(session.Progress, 95);
                    session.Speed = "Merging...";
                    session.ETA = "00:01";

                    _logger.LogInformation("Session {SessionId}: Merging video and audio streams", session.Id);
                }
                else if (infoMatch.Success)
                {
                    _logger.LogDebug("Session {SessionId}: {Info}", session.Id, infoMatch.Groups[1].Value);
                }

                // Send WebSocket update if connected
                if (session.WebSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var update = JsonSerializer.Serialize(new
                    {
                        sessionId = session.Id,
                        progress = session.Progress,
                        speed = session.Speed,
                        eta = session.ETA,
                        fileSize = session.FileSize,
                        status = session.Status.ToString().ToLower()
                    });

                    var bytes = System.Text.Encoding.UTF8.GetBytes(update);
                    _ = session.WebSocket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        System.Net.WebSockets.WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing progress update: {Output}", output);
            }
        }

        private static bool IsLargerFileSize(string newSize, string currentSize)
        {
            try
            {
                // Simple comparison - extract numbers and compare
                var newSizeMatch = Regex.Match(newSize, @"(\d+\.?\d*)");
                var currentSizeMatch = Regex.Match(currentSize, @"(\d+\.?\d*)");

                if (newSizeMatch.Success && currentSizeMatch.Success &&
                    double.TryParse(newSizeMatch.Groups[1].Value, out var newNum) &&
                    double.TryParse(currentSizeMatch.Groups[1].Value, out var currentNum))
                {
                    // If both are same unit (MiB, GiB), compare directly
                    if (newSize.Contains("GiB") && currentSize.Contains("MiB")) return true;
                    if (newSize.Contains("MiB") && currentSize.Contains("GiB")) return false;

                    return newNum > currentNum;
                }
            }
            catch
            {
                // If comparison fails, keep the new size
            }

            return true;
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

            var result = await RunYtDlpWithCookiesAsync(arguments);
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

        //// Keep existing helper methods...
        //private async Task<YtDlpResult> RunYtDlpAsync(List<string> arguments)
        //{
        //    var startInfo = new ProcessStartInfo
        //    {
        //        FileName = _ytDlpPath,
        //        Arguments = string.Join(" ", arguments),
        //        UseShellExecute = false,
        //        RedirectStandardOutput = true,
        //        RedirectStandardError = true,
        //        CreateNoWindow = true
        //    };

        //    using var process = new Process { StartInfo = startInfo };

        //    var outputBuilder = new System.Text.StringBuilder();
        //    var errorBuilder = new System.Text.StringBuilder();

        //    process.OutputDataReceived += (sender, e) => {
        //        if (e.Data != null) outputBuilder.AppendLine(e.Data);
        //    };

        //    process.ErrorDataReceived += (sender, e) => {
        //        if (e.Data != null) errorBuilder.AppendLine(e.Data);
        //    };

        //    process.Start();
        //    process.BeginOutputReadLine();
        //    process.BeginErrorReadLine();

        //    await process.WaitForExitAsync();

        //    return new YtDlpResult
        //    {
        //        Success = process.ExitCode == 0,
        //        Output = outputBuilder.ToString(),
        //        Error = errorBuilder.ToString(),
        //        ExitCode = process.ExitCode
        //    };
        //}


        // Fix 2: Update RunYtDlpWithCookiesAsync for Docker (no browser cookies available)
        private async Task<YtDlpResult> RunYtDlpWithCookiesAsync(List<string> arguments)
        {
            var enhancedArgs = new List<string>();

            // Docker-compatible anti-detection measures (no browser cookies)
            enhancedArgs.Add("--user-agent");
            enhancedArgs.Add("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            enhancedArgs.Add("--referer");
            enhancedArgs.Add("https://www.youtube.com/");

            // Add headers to mimic real browser behavior
            enhancedArgs.Add("--add-header");
            enhancedArgs.Add("Accept:text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");

            enhancedArgs.Add("--add-header");
            enhancedArgs.Add("Accept-Language:en-US,en;q=0.9");

            enhancedArgs.Add("--add-header");
            enhancedArgs.Add("Accept-Encoding:gzip, deflate, br");

            // Network and retry settings
            enhancedArgs.AddRange(new[]
            {
        "--sleep-interval", "1",
        "--max-sleep-interval", "5",
        "--extractor-retries", "3",
        "--socket-timeout", "30",
        "--fragment-retries", "10"
    });

            enhancedArgs.AddRange(arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Use ArgumentList for proper escaping
            startInfo.ArgumentList.Clear();
            foreach (var arg in enhancedArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

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
                // Try to get specific resolution, but be more aggressive about limiting quality
                "240p" => "worst[height<=240][ext=mp4]/worst[height<=240]/worst[ext=mp4]/worst",
                "360p" => "best[height<=360][height>=240][ext=mp4]/best[height<=360][ext=mp4]/worst[height>240]",
                "480p" => "best[height<=480][height>=360][ext=mp4]/best[height<=480][ext=mp4]/best[height<=480]",
                "720p" => "best[height<=720][height>=480][ext=mp4]/best[height<=720][ext=mp4]/best[height<=720]",
                "1080p" => "best[height<=1080][height>=720][ext=mp4]/best[height<=1080][ext=mp4]/best[height<=1080]",
                "1440p" => "best[height<=1440][height>=1080][ext=mp4]/best[height<=1440][ext=mp4]/best[height<=1440]",
                "4k" => "best[height<=2160][height>=1440][ext=mp4]/best[height<=2160][ext=mp4]/best[height<=2160]",

                // More explicit format selection
                "small" => "worst[ext=mp4]/worst",
                "medium" => "best[height<=480][ext=mp4]/best[height<=480]",
                "large" => "best[height<=1080][ext=mp4]/best[height<=1080]",

                // Audio only
                "audio" => "bestaudio[ext=m4a]/bestaudio[ext=mp3]/bestaudio",


                // Bandwidth-conscious options
                "low-bandwidth" => "worst[ext=mp4]/worst",
                "mobile" => "best[height<=480][ext=mp4]/best[height<=480]",

                // Original options (kept for compatibility)
                "best" => "best[ext=mp4]/best",
                "best-merge" => "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio",
                "worst" => "worst[ext=mp4]/worst",

                // Default fallback
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