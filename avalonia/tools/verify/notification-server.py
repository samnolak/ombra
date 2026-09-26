"""A freedesktop notification server stand-in (org.freedesktop.Notifications on the session bus) that records every
Notify call as one JSON line, for checking what the client sends through notify-send / libnotify on a machine without a
desktop shell. It shows nothing; delivery to a real desktop's notification area is a separate check.

    python notification-server.py <log.jsonl>     (needs dbus-next; DBUS_SESSION_BUS_ADDRESS set)
"""
import asyncio, json, sys, time
from dbus_next.aio import MessageBus
from dbus_next.service import ServiceInterface, method

LOG = sys.argv[1]


class Notifications(ServiceInterface):
    def __init__(self):
        super().__init__("org.freedesktop.Notifications")
        self.next_id = 1

    @method()
    def GetCapabilities(self) -> "as":
        return ["body"]

    @method()
    def GetServerInformation(self) -> "ssss":
        return ["ompgui-notification-check", "ompgui", "1", "1.2"]

    @method()
    def Notify(self, app_name: "s", replaces_id: "u", app_icon: "s", summary: "s", body: "s", actions: "as",
               hints: "a{sv}", expire_timeout: "i") -> "u":
        with open(LOG, "a") as f:
            f.write(json.dumps({"at": time.time(), "app": app_name, "summary": summary, "body": body}) + "\n")
        self.next_id += 1
        return self.next_id

    @method()
    def CloseNotification(self, id: "u"):
        pass


async def main():
    bus = await MessageBus().connect()
    bus.export("/org/freedesktop/Notifications", Notifications())
    await bus.request_name("org.freedesktop.Notifications")
    print("notification server ready", flush=True)
    await asyncio.Future()

asyncio.run(main())
