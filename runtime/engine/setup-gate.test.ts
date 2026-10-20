import { expect, test } from "bun:test";
import { CURRENT_SETUP_VERSION } from "@oh-my-pi/pi-tui/setup/setup-version";
import { Settings } from "./coding-agent/src/config/settings";
import { ALL_SCENES, selectSetupScenes } from "./coding-agent/src/modes/setup";
import { adoptConfiguredProfile } from "./coding-agent/src/modes/setup-gate";
import { cfgSetupVersion } from "./coding-agent/src/modes/settings";

const tty = { isTTY: true, setupWizardEnabled: true, skipEnv: "" };

test("a profile set up outside the wizard (models available, no setup version) gets no setup scenes", async () => {
	const settings = Settings.isolated();
	expect(cfgSetupVersion.get(settings)).toBe(0);
	const version = await adoptConfiguredProfile(settings, 3, false);
	expect(version).toBe(CURRENT_SETUP_VERSION);
	expect(cfgSetupVersion.get(settings)).toBe(CURRENT_SETUP_VERSION);
	expect(await selectSetupScenes(version, ALL_SCENES, undefined, tty)).toEqual([]);
});

test("a fresh profile without models still runs the wizard from its first scene", async () => {
	const settings = Settings.isolated();
	const version = await adoptConfiguredProfile(settings, 0, false);
	expect(version).toBe(0);
	expect(cfgSetupVersion.get(settings)).toBe(0);
	const scenes = await selectSetupScenes(version, ALL_SCENES.filter(scene => !scene.shouldRun), undefined, tty);
	expect(scenes.length).toBeGreaterThan(0);
	expect(scenes[0]!.minVersion).toBe(1);
});

test("an older wizard run and `omp setup` keep their scenes", async () => {
	const older = Settings.isolated();
	cfgSetupVersion.set(older, 1);
	expect(await adoptConfiguredProfile(older, 3, false)).toBe(1);
	const forced = Settings.isolated();
	expect(await adoptConfiguredProfile(forced, 3, true)).toBe(0);
	expect(cfgSetupVersion.get(forced)).toBe(0);
});
