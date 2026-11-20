// Shared by the fixture tests: the host (and the fixture) start, ask and review only the AI saved as chosen (onboarding
// provider.kind), so a test that calls agent.start or review.preview directly saves that choice first.
const VALUE = { claude_code_mcp: 'claude-code', chatgpt_plan: 'chatgpt', anthropic_api: 'api', openai_api: 'api' };
const LABEL = { claude_code_mcp: 'Claude', chatgpt_plan: 'ChatGPT', anthropic_api: 'Claude (API)', openai_api: 'OpenAI (API)' };
export async function choose(be, kind) {
  const o = await be.call('onboarding.get');
  if (o.profile.provider && o.profile.provider.kind === kind) return;
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { provider: { value: VALUE[kind], label: LABEL[kind], kind } } });
}
