using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;

namespace Athena.UI.Services;

public sealed class ChatSessionFactory
{
    private readonly IChatService _chatService;
    private readonly IConfigService _configService;
    private readonly IPromptService _promptService;
    private readonly ITaskScheduler _taskScheduler;
    private readonly IFunctionRegistry _functionRegistry;
    private readonly ILocalizationService _localizationService;
    private readonly IAttachmentStoreService _attachmentStoreService;
    private readonly ISystemAudioService _systemAudioService;
    private readonly IConversationArchiveService _archiveService;
    private readonly IImageGenerationSessionService _imageSessionService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly ISubAgentOrchestrator _subAgentOrchestrator;
    private readonly IWorkspaceService _workspaceService;
    private readonly IConversationSessionAccessor _sessionAccessor;
    private readonly IUserInteractionService _userInteractionService;
    private readonly ConversationExecutionCoordinator _executionCoordinator;
    private readonly IContextPolicyProvider _contextPolicyProvider;
    private readonly ICompressionPlanner _compressionPlanner;
    private readonly ICompressionCandidateGenerator _compressionCandidateGenerator;
    private readonly ICompressionValidator _compressionValidator;

    public ChatSessionFactory(
        IChatService chatService,
        IConfigService configService,
        IPromptService promptService,
        ITaskScheduler taskScheduler,
        IFunctionRegistry functionRegistry,
        ILocalizationService localizationService,
        IAttachmentStoreService attachmentStoreService,
        ISystemAudioService systemAudioService,
        IConversationArchiveService archiveService,
        IImageGenerationSessionService imageSessionService,
        IScreenCaptureService screenCaptureService,
        ISubAgentOrchestrator subAgentOrchestrator,
        IWorkspaceService workspaceService,
        IConversationSessionAccessor sessionAccessor,
        IUserInteractionService userInteractionService,
        ConversationExecutionCoordinator executionCoordinator,
        IContextPolicyProvider contextPolicyProvider,
        ICompressionPlanner compressionPlanner,
        ICompressionCandidateGenerator compressionCandidateGenerator,
        ICompressionValidator compressionValidator)
    {
        _chatService = chatService;
        _configService = configService;
        _promptService = promptService;
        _taskScheduler = taskScheduler;
        _functionRegistry = functionRegistry;
        _localizationService = localizationService;
        _attachmentStoreService = attachmentStoreService;
        _systemAudioService = systemAudioService;
        _archiveService = archiveService;
        _imageSessionService = imageSessionService;
        _screenCaptureService = screenCaptureService;
        _subAgentOrchestrator = subAgentOrchestrator;
        _workspaceService = workspaceService;
        _sessionAccessor = sessionAccessor;
        _userInteractionService = userInteractionService;
        _executionCoordinator = executionCoordinator;
        _contextPolicyProvider = contextPolicyProvider;
        _compressionPlanner = compressionPlanner;
        _compressionCandidateGenerator = compressionCandidateGenerator;
        _compressionValidator = compressionValidator;
    }

    public MainConversationViewModel Create()
    {
        var tokenService = new TokenService();
        return new MainConversationViewModel(
            _chatService,
            _configService,
            null,
            _promptService,
            _taskScheduler,
            _functionRegistry,
            tokenService,
            _localizationService,
            _attachmentStoreService,
            _systemAudioService,
            _archiveService,
            _imageSessionService,
            _screenCaptureService,
            _subAgentOrchestrator,
            _workspaceService,
            _sessionAccessor,
            _userInteractionService,
            _executionCoordinator,
            _contextPolicyProvider,
            _compressionPlanner,
            _compressionCandidateGenerator,
            _compressionValidator);
    }
}
