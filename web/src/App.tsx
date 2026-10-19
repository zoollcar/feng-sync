import { useEffect } from "react";
import { PairScreen } from "./components/PairScreen";
import { RoomScreen } from "./components/RoomScreen";
import { IconAlert, IconMonitor } from "./components/icons";
import { useSession } from "./hooks/useSession";
import { t } from "./i18n/strings";

/**
 * Which of the page's three shapes to draw.
 *
 * The decision is the protocol's, not a router's: `/hello` and `/peers` between them answer whether this browser is
 * already somebody the computer knows, and there is no third state to navigate to. A router would add an address
 * bar the phone never sees and a history the back button would walk through.
 */
export default function App() {
  const session = useSession();

  useEffect(() => {
    const name = session.hello?.deviceName;
    if (name) document.title = `${t("title")} · ${name}`;
  }, [session.hello]);

  if (session.phase === "loading") return <Splash />;
  if (session.phase === "fatal") return <Fatal message={session.error} />;
  if (session.phase === "pair") {
    return (
      <PairScreen
        computer={session.hello?.deviceName ?? ""}
        host={location.host}
        initialCode={session.prefilled}
        error={session.error}
        onPair={session.pair}
      />
    );
  }

  return <RoomScreen session={session} />;
}

function Splash() {
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center gap-4">
      <span className="grid h-14 w-14 place-items-center rounded-2xl bg-surface text-accent shadow-card">
        <IconMonitor className="h-6 w-6" />
      </span>
      <p className="text-xs text-muted">{t("connecting")}</p>
    </div>
  );
}

/** The one dead end: the address answered, but not as the computer the code named. */
function Fatal({ message }: { message: string }) {
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center px-6">
      <div className="w-full max-w-sm rounded-card border border-line bg-surface p-5 text-center shadow-card">
        <span className="mx-auto grid h-12 w-12 place-items-center rounded-2xl bg-danger/5 text-danger">
          <IconAlert className="h-5 w-5" />
        </span>
        <p className="mt-3 text-sm leading-relaxed">{message}</p>
        <button
          type="button"
          onClick={() => location.reload()}
          className="mt-4 h-11 w-full rounded-xl bg-accent text-sm font-semibold text-white transition-colors hover:bg-accent-ink"
        >
          {t("retry")}
        </button>
      </div>
    </div>
  );
}
