/*
 * Migairu Europa - End-to-End Encrypted File Transfer
 * Copyright (C) 2024 Migairu Corp.
 * Written by Juan Miguel Giraldo.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License along
 * with this program; if not, see <https://www.gnu.org/licenses/>.
 */

using Europa.Models;
using Europa.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;

[ApiController]
[Route("api/upload")]
public class ChunkedUploadController : ControllerBase
{
    private readonly ILogger<ChunkedUploadController> _logger;
    private readonly ChunkService _chunkService;
    private readonly IMemoryCache _cache;
    private const long DEFAULT_MAX_FILE_SIZE = 2L * 1024 * 1024 * 1024;

    public ChunkedUploadController(
        ILogger<ChunkedUploadController> logger,
        ChunkService chunkService,
        IMemoryCache cache)
    {
        _logger = logger;
        _chunkService = chunkService;
        _cache = cache;
    }

    [HttpPost("init")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InitializeUpload([FromBody] InitUploadRequest request)
    {
        try
        {
            if (request.TotalSize > DEFAULT_MAX_FILE_SIZE)
            {
                return BadRequest($"Total file size exceeds the maximum allowed size of {DEFAULT_MAX_FILE_SIZE / (1024.0 * 1024 * 1024):F2} GB.");
            }

            if (!int.TryParse(request.ExpirationOption, out int expirationDays) ||
                expirationDays < 1 || expirationDays > 30)
            {
                return BadRequest("Expiration must be between 1 and 30 days.");
            }

            string fileId = await GenerateSecureFileIdAsync();
            string uploadId = await _chunkService.InitializeUpload(
                fileId,
                request.TotalChunks,
                request.TotalSize,
                request.Iv,
                request.Salt,
                request.FileName.EndsWith(".zip"),
                request.ExpirationOption
            );

            return Ok(new { uploadId, fileId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing upload");
            return StatusCode(500, "An error occurred while initializing the upload.");
        }
    }

    [HttpPost("chunk")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadChunk([FromForm] UploadChunkRequest request)
    {
        try
        {
            if (request.Chunk == null || request.Chunk.Length == 0)
                return BadRequest("No chunk data provided.");

            using var ms = new MemoryStream();
            await request.Chunk.CopyToAsync(ms);
            var chunkData = ms.ToArray();

            bool success = await _chunkService.UploadChunk(
                request.UploadId,
                request.ChunkNumber,
                chunkData
            );

            if (!success)
            {
                return BadRequest("Failed to process chunk.");
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading chunk");
            return StatusCode(500, "An error occurred while uploading the chunk.");
        }
    }

    [HttpPost("finalize")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FinalizeUpload([FromBody] FinalizeUploadRequest request)
    {
        try
        {
            var uploadStatus = _cache.Get<UploadStatus>($"upload_{request.UploadId}");
            if (uploadStatus == null)
            {
                return BadRequest("Upload session not found.");
            }

            if (!int.TryParse(uploadStatus.ExpirationOption, out int expirationDays) ||
                expirationDays < 1 || expirationDays > 30)
            {
                return BadRequest("Invalid expiration days.");
            }

            bool success = await _chunkService.FinalizeUpload(
                request.UploadId,
                request.FileId,
                expirationDays
            );

            if (!success)
            {
                return BadRequest("Failed to finalize upload.");
            }

            var downloadUrl = $"/d/{request.FileId}";
            return Ok(new { DownloadUrl = downloadUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizing upload");
            return StatusCode(500, "An error occurred while finalizing the upload.");
        }
    }

    private async Task<string> GenerateSecureFileIdAsync()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return await Task.FromResult(Convert.ToBase64String(bytes)
            .Replace("/", "_")
            .Replace("+", "-")
            .TrimEnd('='));
    }
}
public class InitUploadRequest
{
    public string FileName { get; set; }
    public int TotalChunks { get; set; }
    public long TotalSize { get; set; }
    public string ExpirationOption { get; set; }
    public string Iv { get; set; }
    public string Salt { get; set; }
}

public class UploadChunkRequest
{
    public IFormFile Chunk { get; set; }
    public string UploadId { get; set; }
    public int ChunkNumber { get; set; }
}

public class FinalizeUploadRequest
{
    public string UploadId { get; set; }
    public string FileId { get; set; }
    public string FileName { get; set; }
}