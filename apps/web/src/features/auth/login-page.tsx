import { type FormEvent, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useSession } from "./session-provider";

export function LoginPage() {
  const [profileId, setProfileId] = useState("maria");
  const { error, signIn, status } = useSession();
  const navigate = useNavigate();

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (await signIn(profileId)) {
      navigate("/protected", { replace: true });
    }
  }

  return (
    <main>
      <h1>Entrar</h1>
      <p>Entrada interna do protótipo. Não é autenticação de produção.</p>
      <form onSubmit={submit}>
        <label htmlFor="development-profile">Perfil de desenvolvimento</label>
        <input
          id="development-profile"
          onChange={(event) => setProfileId(event.target.value)}
          value={profileId}
        />
        <button disabled={status === "pending"} type="submit">
          Entrar
        </button>
      </form>
      {error ? <p role="alert">{error}</p> : null}
    </main>
  );
}
