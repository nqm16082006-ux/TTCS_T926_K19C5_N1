using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Middlewares;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller hỗ trợ tải lên hình ảnh sự kiện, poster và tài nguyên đa phương tiện.
    /// </summary>
    [ApiController]
    [Route("api/upload")]
    [RequireRole("Organizer", "Admin")]
    public class UploadController : ControllerBase
    {
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration? _configuration;
        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        private const long MaxFileSize = 10 * 1024 * 1024; // 10MB

        public UploadController(IWebHostEnvironment environment, IConfiguration? configuration = null)
        {
            _environment = environment;
            _configuration = configuration;
        }

        /// <summary>
        /// Tải lên ảnh poster hoặc banner sự kiện.
        /// POST /api/upload/image
        /// </summary>
        [HttpPost("image")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> UploadImage([FromForm] IFormFile? file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Vui lòng chọn tệp ảnh để tải lên."));
            }

            if (file.Length > MaxFileSize)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Dung lượng tệp ảnh không được vượt quá 10MB."));
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
            {
                return BadRequest(ApiResponse<object>.FailureResult("Định dạng tệp không hợp lệ. Chỉ chấp nhận các định dạng: .jpg, .jpeg, .png, .webp, .gif."));
            }

            var webRoot = _environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var uploadsDir = Path.Combine(_configuration?["Uploads:Path"] ?? Path.Combine(webRoot, "uploads"), "events");

            if (!Directory.Exists(uploadsDir))
            {
                Directory.CreateDirectory(uploadsDir);
            }

            var uniqueFileName = $"{Guid.NewGuid():N}{extension}";
            var filePath = Path.Combine(uploadsDir, uniqueFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var relativeUrl = $"/uploads/events/{uniqueFileName}";
            return Ok(ApiResponse<object>.SuccessResult(new
            {
                url = relativeUrl,
                fileName = file.FileName,
                size = file.Length
            }, "Tải lên hình ảnh thành công."));
        }
    }
}
