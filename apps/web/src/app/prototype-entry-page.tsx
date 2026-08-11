import { useNavigate } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";

export function PrototypeEntryPage() {
  const { bootstrap, signOut, status } = useSession();
  const navigate = useNavigate();

  if (!bootstrap) {
    return null;
  }

  async function leavePrototype() {
    await signOut();
    navigate("/login", { replace: true });
  }

  return (
    <main>
      <h1>Protótipo autenticado</h1>
      <p>{bootstrap.user.name}</p>
      <ul aria-label="Empresas autorizadas">
        {bootstrap.companies.map((company) => (
          <li key={company.id}>
            {company.name}: {company.capabilities.join(", ")}
          </li>
        ))}
      </ul>
      <button
        disabled={status === "pending"}
        onClick={leavePrototype}
        type="button"
      >
        Sair do protótipo
      </button>
    </main>
  );
}
