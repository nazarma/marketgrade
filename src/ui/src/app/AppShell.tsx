import { Outlet } from "react-router";
import { Sidebar } from "@/app/Sidebar";

export function AppShell() {
  return (
    // The window does not scroll; the screen next to the sidebar does.
    <div className="flex h-screen overflow-hidden">
      <Sidebar />
      <main className="flex min-w-0 flex-1 flex-col gap-5 overflow-y-auto px-8 pt-6 pb-10 *:shrink-0">
        <Outlet />
      </main>
    </div>
  );
}
