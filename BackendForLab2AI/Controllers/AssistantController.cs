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
        private static readonly Dictionary<string, ConversationState> _conversations = new Dictionary<string, ConversationState>();

        public AssistantController(IAssistantService assistantService, ILogger<AssistantController> logger)
        {
            _assistantService = assistantService;
            _logger = logger;

            // Инициализация дефолтной беседы
            InitializeDefaultConversation();
        }

        [HttpPost("chat")]
        public async Task<ActionResult<AssistantResponse>> Chat([FromBody] AssistantRequest request)
        {
            try
            {
                string conversationId = request.ConversationId ?? "single-conversation";

                // Получаем или создаем беседу
                if (!_conversations.TryGetValue(conversationId, out var conversation))
                {
                    conversation = CreateConversationInternal(conversationId);
                    _conversations[conversationId] = conversation;
                    _logger.LogInformation($"Created new conversation: {conversationId}");
                }

                if (conversation == null)
                {
                    _logger.LogError("Conversation is null after creation");
                    return StatusCode(500, "Failed to create conversation");
                }

                var response = await _assistantService.ProcessMessageAsync(request, conversation);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in assistant chat");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpPost("conversation/new")]
        public ActionResult CreateNewConversation()
        {
            try
            {
                string newConversationId = Guid.NewGuid().ToString();
                var conversation = CreateConversationInternal(newConversationId);

                if (conversation == null)
                {
                    return StatusCode(500, "Failed to create conversation");
                }

                _conversations[newConversationId] = conversation;

                return Ok(new
                {
                    conversationId = newConversationId,
                    message = "New conversation created successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating new conversation");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpPost("conversation/new/{conversationId}")]
        public ActionResult CreateNewConversationWithId(string conversationId)
        {
            try
            {
                if (string.IsNullOrEmpty(conversationId))
                {
                    return BadRequest("Conversation ID cannot be empty");
                }

                if (_conversations.ContainsKey(conversationId))
                {
                    return Conflict(new { message = "Conversation with this ID already exists" });
                }

                var conversation = CreateConversationInternal(conversationId);

                if (conversation == null)
                {
                    return StatusCode(500, "Failed to create conversation");
                }

                _conversations[conversationId] = conversation;

                return Ok(new
                {
                    conversationId = conversationId,
                    message = "New conversation created successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating new conversation");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("conversation/{conversationId}")]
        public async Task<ActionResult<ConversationState>> GetConversation(string conversationId)
        {
            try
            {
                // Сначала проверяем в локальном словаре
                if (_conversations.TryGetValue(conversationId, out var localConversation))
                {
                    return Ok(localConversation);
                }

                // Если нет в локальном словаре, пробуем получить из сервиса
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
                // Удаляем из локального словаря
                _conversations.Remove(conversationId);

                // Удаляем через сервис
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

        [HttpGet("conversations")]
        public ActionResult<IEnumerable<string>> GetAllConversations()
        {
            try
            {
                var conversationIds = _conversations.Keys.ToList();
                return Ok(conversationIds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting conversations list");
                return StatusCode(500, "Internal server error");
            }
        }

        // Вспомогательный метод для создания беседы
        private ConversationState CreateConversationInternal(string conversationId)
        {
            try
            {
                var conversation = new ConversationState
                {
                    ConversationId = conversationId
                };

                conversation.Messages.Add(new Message
                {
                    Role = "system",
                    Content = "You are a movie recommendation assistant. Always find and recommend exactly 5 movies from the database for every user request."
                });

                return conversation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CreateConversationInternal");
                return null;
            }
        }

        // Инициализация дефолтной беседы
        private void InitializeDefaultConversation()
        {
            try
            {
                var defaultConversation = CreateConversationInternal("single-conversation");
                if (defaultConversation != null)
                {
                    _conversations["single-conversation"] = defaultConversation;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing default conversation");
            }
        }
    }
}