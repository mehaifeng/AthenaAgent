# Athena UI

Athena UI is a desktop AI assistant context centered on user conversations, tool use, and persisted conversational artifacts. This glossary captures the user-facing concepts that shape how conversation and generated media are discussed in the product.

## Language

**Image Generation Session**:
A per-conversation continuity record for image generation that tracks the current visual lineage across multiple image requests in the same main chat.
_Avoid_: image chat, image thread, image conversation

**Main Conversation**:
The primary user-visible chat session. Its model can converse and delegate execution to the Tool Agent, but does not directly carry the full built-in/MCP tool catalog.
_Avoid_: image session, render session

**Provider Profile**:
One reusable OpenAI SDK-compatible connection (display name, Base URL, and API key) selected by one or more model roles. TTS and image generation connections are extension-specific and are not Provider Profiles.
_Avoid_: secondary credential, inherited API key

**Tool Agent**:
An isolated, main-conversation-controlled execution agent that owns the built-in and MCP tool catalog. Its direct calls are projected into the active assistant bubble as execution trace cards, while its private messages stay out of the Main Conversation context.
_Avoid_: direct main-model tool call, browser agent

## Example Dialogue

Dev: When the user says "change the last image a bit", which state should I read?
Domain Expert: Read the active Image Generation Session for the current Main Conversation.

Dev: So the Main Conversation owns the Image Generation Session?
Domain Expert: Yes. The chat is the container, and the image session carries visual continuity inside that chat.
