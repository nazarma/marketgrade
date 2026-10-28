import { exchangeInfo } from "@/lib/exchanges";

export function ChecksPanel({ ids }: { ids: number[] }) {
  return (
    <section className="panel flex min-w-0 flex-[1_1_320px] flex-col gap-3.5 px-5 py-[18px]">
      <h2 className="h2">How each exchange is checked</h2>
      <div className="flex flex-col gap-3 text-[13px]">
        {ids.map((id) => {
          const info = exchangeInfo(id);
          return (
            <div key={id}>
              <div className="font-medium">{info.name}</div>
              <div className="lbl mt-0.5">{info.check}</div>
            </div>
          );
        })}
      </div>
      <p className="lbl m-0">
        When a check fails the book is marked invalid, the gap is logged and the book is rebuilt by
        itself.
      </p>
    </section>
  );
}
