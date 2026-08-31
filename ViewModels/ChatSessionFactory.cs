using Athena.UI.Services;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.ViewModels;

/// <summary>
/// 会话 ViewModel 的生产装配点。它做的是组合根的活儿——把 19 个服务拼成一个
/// MainConversationViewModel——所以属于展示层，不属于 Services：服务层的签名里
/// 不该出现 ViewModel（见 CLAUDE.md「Review Rules」第 4 条）。
/// 这里传进去的每个依赖都是非空的，因此产物的
/// <see cref="MainConversationViewModel.MissingCriticalDependencies"/> 必须为空集。
/// </summary>
public sealed class ChatSessionFactory
{
    private readonly IChatService _chatService;
    private readonly IConfigService _configService;
    private readonly IPromptService _promptService;
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
    private readonly IVirtualPetProgressionService _petProgressionService;
    private readonly IPetChatterService _petChatterService;

    public ChatSessionFactory(
        IChatService chatService,
        IConfigService configService,
        IPromptService promptService,
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
        ICompressionValidator compressionValidator,
        IVirtualPetProgressionService petProgressionService,
        IPetChatterService petChatterService)
    {
        _chatService = chatService;
        _configService = configService;
        _promptService = promptService;
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
        _petProgressionService = petProgressionService;
        _petChatterService = petChatterService;
    }

    public MainConversationViewModel Create()
    {
        var tokenService = new TokenService();
        return new MainConversationViewModel(
            _chatService,
            _configService,
            null,
            _promptService,
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
            _compressionValidator,
            _petProgressionService,
            _petChatterService);
    }
}
