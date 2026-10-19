/** A saved model remains unavailable after provider and extension discovery. */
export class SessionModelUnavailableError extends Error {
	readonly code = "session_model_unavailable";
	readonly data: { provider: string; modelId: string; selector: string };

	constructor(selector: string) {
		super(`Could not restore model ${selector}`);
		this.name = "SessionModelUnavailableError";
		const slash = selector.indexOf("/");
		this.data = { provider: slash < 0 ? "" : selector.slice(0, slash), modelId: slash < 0 ? selector : selector.slice(slash + 1), selector };
	}
}
