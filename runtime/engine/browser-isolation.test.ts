import { expect, test } from "bun:test";
import { AgentRegistry } from "./coding-agent/src/registry/agent-registry";
import { getBrowsersMapForTest } from "./coding-agent/src/tools/browser/registry";
import type { TernBrowserHandle } from "./coding-agent/src/tools/browser/registry";
import { getTab, getTabsMapForTest, listTabs, releaseAllTabs, releaseTabsForOwner } from "./coding-agent/src/tools/browser/tab-supervisor";
import type { TernTabSession } from "./coding-agent/src/tools/browser/tab-supervisor";
import { TernTab } from "./coding-agent/src/tools/browser/tern/tern-tab";
import { TernSocketClient } from "./coding-agent/src/tools/browser/tern/wire";

function makeTab(owner: string, block: number) {
	let tabClosed = false;
	let socketClosed = false;
	const socket = new TernSocketClient({ socketPath: "/test-only-never-connected" });
	socket.close = () => { socketClosed = true; };
	const viewport = { width: 800, height: 600 };
	const ternTab = new TernTab({ client: socket, block, name: "same-name", viewport });
	ternTab.close = async () => { tabClosed = true; };
	const browser: TernBrowserHandle = {
		key: "tern:same-pane", kind: { kind: "tern", socketPath: "/test-only-never-connected", pane: 1 },
		tern: socket, refCount: 1,
	};
	const tab: TernTabSession = {
		name: "same-name", backend: "tern", browser, targetId: String(block), state: "alive",
		info: { url: `https://${owner}.test`, viewport, targetId: String(block) },
		pending: new Map(), kindTag: "tern", ownerSessionId: owner,
		lastActivityAt: Date.now(), frozen: false, ternTab,
	};
	return { tab, closed: () => ({ tabClosed, socketClosed }) };
}

test("identical browser names and pane metadata stay private to logical chat trees", async () => {
	const registryA = new AgentRegistry();
	const registryB = new AgentRegistry();
	const a = makeTab("runtime-A", 11);
	const b = makeTab("runtime-B", 12);
	const mapA = AgentRegistry.runWithRegistry(registryA, () => {
		getTabsMapForTest().set(a.tab.name, a.tab);
		return getBrowsersMapForTest();
	});
	const mapB = AgentRegistry.runWithRegistry(registryB, () => {
		getTabsMapForTest().set(b.tab.name, b.tab);
		return getBrowsersMapForTest();
	});
	expect(mapA).not.toBe(mapB);
	expect(AgentRegistry.runWithRegistry(registryA, () => getTab("same-name"))).toBe(a.tab);
	expect(AgentRegistry.runWithRegistry(registryB, () => listTabs()[0]?.targetId)).toBe("12");
	await AgentRegistry.runWithRegistry(registryA, () => releaseTabsForOwner("runtime-A", { kill: true }));
	expect(a.closed()).toEqual({ tabClosed: true, socketClosed: true });
	expect(b.closed()).toEqual({ tabClosed: false, socketClosed: false });
	expect(AgentRegistry.runWithRegistry(registryB, () => getTab("same-name")?.state)).toBe("alive");
	await AgentRegistry.runWithRegistry(registryB, () => releaseAllTabs({ kill: true }));
	expect(b.closed()).toEqual({ tabClosed: true, socketClosed: true });
});

test("browser close all cannot see another logical session's tabs", async () => {
	const registryA = new AgentRegistry();
	const registryB = new AgentRegistry();
	const b = makeTab("runtime-B", 13);
	AgentRegistry.runWithRegistry(registryB, () => getTabsMapForTest().set(b.tab.name, b.tab));
	expect(await AgentRegistry.runWithRegistry(registryA, () => releaseAllTabs({ kill: true }))).toBe(0);
	expect(b.closed()).toEqual({ tabClosed: false, socketClosed: false });
	await AgentRegistry.runWithRegistry(registryB, () => releaseAllTabs({ kill: true }));
});
