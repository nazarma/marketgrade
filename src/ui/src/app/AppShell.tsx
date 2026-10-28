import { Outlet } from "react-router";
import { Sidebar } from "@/app/Sidebar";

export function AppShell() {
  return (
    <div className="flex min-h-screen">
      <Sidebar />
      <main className="flex min-w-0 flex-1 flex-col gap-5 px-8 pt-6 pb-10">
        <Outlet />
      </main>
    </div>
  );
}
