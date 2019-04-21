using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Services;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [Authorize("Member")]
    public class DocumentController : Controller
    {
        private readonly DocumentService _documentService;

        public DocumentController(DocumentService documentService)
        {
            _documentService = documentService;
        }

        [HttpGet]
        public async Task<ActionResult> GetHierarchy()
        {
            var result = await _documentService.GetHierarchy();
            return Json(result);
        }

        [HttpGet("{*path}")]
        public async Task<ActionResult> GetFile(string path)
        {
            var (stream, contentType) = await _documentService.GetDocument(path);
            stream.Position = 0;
            return new FileStreamResult(stream, contentType);
        }
    }
}
