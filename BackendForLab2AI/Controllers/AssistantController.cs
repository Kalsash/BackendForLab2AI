// AssistantController.cs
using Microsoft.AspNetCore.Mvc;
using BackendForLab2AI.Services;
using BackendForLab2AI.Models;

namespace BackendForLab2AI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AssistantController : ControllerBase
    {
        private readonly IAssistantService _assistantService;
        private readonly ILogger<AssistantController> _logger;

        public AssistantController(IAssistantService assistantService, ILogger<AssistantController> logger)
        {
            _assistantService = assistantService;
            _logger = logger;
        }

        [HttpPost("chat")]
        public async Task<ActionResult<AssistantResponse>> Chat([FromBody] AssistantRequest request)
        {
            try
            {
                var response = await _assistantService.ProcessMessageAsync(request);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in assistant chat");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("conversation/{conversationId}")]
        public async Task<ActionResult<ConversationState>> GetConversation(string conversationId)
        {
            try
            {
                var conversation = await _assistantService.GetConversationStateAsync(conversationId);
                if (conversation == null)
                    return NotFound();

                return Ok(conversation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting conversation");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpDelete("conversation/{conversationId}")]
        public async Task<ActionResult> DeleteConversation(string conversationId)
        {
            try
            {
                var result = await _assistantService.DeleteConversationAsync(conversationId);
                if (!result)
                    return NotFound();

                return Ok(new { message = "Conversation deleted" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting conversation");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}