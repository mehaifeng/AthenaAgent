# Athena UI

Athena UI is a desktop AI assistant context centered on user conversations, tool use, and persisted conversational artifacts. This glossary captures the user-facing concepts that shape how conversation and generated media are discussed in the product.

## Language

**Image Generation Session**:
A per-conversation continuity record for image generation that tracks the current visual lineage across multiple image requests in the same main chat.
_Avoid_: image chat, image thread, image conversation

**Main Conversation**:
The primary user-visible chat session that contains normal assistant replies, tool use, and zero or more image generation sessions.
_Avoid_: image session, render session

## Example Dialogue

Dev: When the user says "change the last image a bit", which state should I read?
Domain Expert: Read the active Image Generation Session for the current Main Conversation.

Dev: So the Main Conversation owns the Image Generation Session?
Domain Expert: Yes. The chat is the container, and the image session carries visual continuity inside that chat.
