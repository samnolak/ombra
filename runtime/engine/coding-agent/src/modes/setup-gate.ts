import { CURRENT_SETUP_VERSION } from "@oh-my-pi/pi-tui/setup/setup-version";
import type { Settings } from "../config/settings";
import { cfgSetupVersion } from "./settings";

/**
 * The setup version to select onboarding scenes with. A profile that never ran the setup wizard (no stored version)
 * but already has a model to use was set up elsewhere — the Ombra app, `omp login`, provider keys in the environment.
 * Its first scene would sign in again (Enter starts the first provider's login), so the profile is recorded as set up
 * and no scene runs. A profile that ran an older wizard keeps getting the scenes added since.
 */
export async function adoptConfiguredProfile(settings: Settings, availableModels: number, force: boolean): Promise<number> {
	const stored = cfgSetupVersion.get(settings);
	if (force || stored > 0 || availableModels === 0) return stored;
	cfgSetupVersion.set(settings, CURRENT_SETUP_VERSION);
	await settings.flush();
	return CURRENT_SETUP_VERSION;
}
