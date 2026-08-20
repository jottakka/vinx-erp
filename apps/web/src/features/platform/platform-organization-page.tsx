import { zodResolver } from "@hookform/resolvers/zod";
import {
  Alert,
  Button,
  Card,
  CardContent,
  CircularProgress,
  Stack,
  TextField,
} from "@mui/material";
import { useMutation } from "@tanstack/react-query";
import type { MutableRefObject, ReactElement } from "react";
import { useEffect, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { PageHeader } from "../../shared/ui/page-header";
import { useSession } from "../auth/session-provider";
import {
  createPlatformOrganizationClient,
  type PlatformOrganizationFailure,
  type PlatformOrganizationGateway,
  type PlatformOrganizationInvitationStatus,
  type PlatformOrganizationProvisionCommand,
} from "./platform-organization-client";

const organizationFormSchema = z.object({
  groupName: z
    .string()
    .trim()
    .min(1, "Informe um nome válido.")
    .max(200, "Informe um nome com até 200 caracteres."),
  companyName: z
    .string()
    .trim()
    .min(1, "Informe um nome válido.")
    .max(200, "Informe um nome com até 200 caracteres."),
  ownerDisplayName: z
    .string()
    .trim()
    .min(1, "Informe um nome válido.")
    .max(200, "Informe um nome com até 200 caracteres."),
  ownerEmail: z
    .string()
    .trim()
    .min(1, "Informe um e-mail válido.")
    .max(320, "Informe um e-mail com até 320 caracteres.")
    .email("Informe um e-mail válido."),
});

type PlatformOrganizationForm = z.infer<typeof organizationFormSchema>;
type PlatformOrganizationPageProps = {
  gateway?: PlatformOrganizationGateway;
  createRequestId?: () => string;
};

type ProvisionOperation = {
  command: PlatformOrganizationProvisionCommand;
  generation: number;
  userId: string;
};

type ProvisionReceipt = {
  state: "created" | "replay";
  invitationStatus: PlatformOrganizationInvitationStatus;
};

type ProvisionFeedback = "conflict" | "rate_limited" | "session" | "error";

const invitationStatusMessage: Record<
  PlatformOrganizationInvitationStatus,
  string
> = {
  pending_acceptance: "Convite pendente de aceite.",
  pending_delivery: "Convite pendente de entrega.",
};

export function PlatformOrganizationPage({
  gateway = createPlatformOrganizationClient(),
  createRequestId = () => crypto.randomUUID(),
}: PlatformOrganizationPageProps): ReactElement {
  const { bootstrap, refreshBootstrap, signOut } = useSession();
  const [gateUserId, setGateUserId] = useState<string | null>(null);
  const [attempt, setAttempt] = useState<ProvisionOperation | null>(null);
  const [receipt, setReceipt] = useState<ProvisionReceipt | null>(null);
  const [feedback, setFeedback] = useState<ProvisionFeedback | null>(null);
  const [mfaBlocked, setMfaBlocked] = useState(false);
  const operationGeneration = useRef(0);
  const inFlight = useRef(false);
  const attemptRef = useRef<ProvisionOperation | null>(null);
  const currentUserIdRef = useRef<string | null>(null);
  const form = useForm<PlatformOrganizationForm>({
    defaultValues: {
      companyName: "",
      groupName: "",
      ownerDisplayName: "",
      ownerEmail: "",
    },
    resolver: zodResolver(organizationFormSchema),
  });

  const isOperator = bootstrap?.platform.isPlatformOperator === true;
  const userId = bootstrap?.user.id;
  currentUserIdRef.current = isOperator ? (userId ?? null) : null;

  // O gate pertence a uma identidade: trocar operador (ou perder o gate)
  // invalida recibo, tentativa pendente e bloqueio de MFA da identidade anterior.
  useEffect(() => {
    const currentGateUserId = isOperator ? (userId ?? null) : null;
    if (currentGateUserId === gateUserId) return;

    operationGeneration.current += 1;
    setAttempt(null);
    attemptRef.current = null;
    setReceipt(null);
    setFeedback(null);
    setMfaBlocked(false);
    form.reset();
    setGateUserId(currentGateUserId);
  }, [form, gateUserId, isOperator, userId]);

  const mutation = useMutation({
    mutationFn: (operation: ProvisionOperation) =>
      gateway.provision(operation.command),
    onSettled: () => {
      inFlight.current = false;
    },
    onSuccess: (result, operation) => {
      if (!isCurrentOperation(operation, operationGeneration, currentUserIdRef))
        return;

      if (result.ok) {
        setAttempt(null);
        attemptRef.current = null;
        setFeedback(null);
        setReceipt({
          state: result.state,
          invitationStatus: result.invitationStatus,
        });
        form.reset();
        return;
      }

      const error = result.error;
      if (error.kind === "mfa_assurance_required") {
        setAttempt(null);
        attemptRef.current = null;
        setReceipt(null);
        setFeedback(null);
        setMfaBlocked(true);
        return;
      }
      if (isAmbiguous(error)) {
        setReceipt(null);
        setFeedback(null);
        setAttempt(operation);
        attemptRef.current = operation;
        return;
      }

      setAttempt(null);
      attemptRef.current = null;
      setReceipt(null);
      if (isAuthorizationFailure(error)) {
        setFeedback("session");
        void refreshBootstrap();
        return;
      }
      setFeedback(feedbackFor(error));
    },
  });

  const reserve = (values: PlatformOrganizationForm) => {
    if (
      !isOperator ||
      !userId ||
      mfaBlocked ||
      attemptRef.current ||
      mutation.isPending ||
      inFlight.current
    ) {
      return;
    }
    const command: PlatformOrganizationProvisionCommand = {
      companyName: values.companyName.trim(),
      groupName: values.groupName.trim(),
      ownerDisplayName: values.ownerDisplayName.trim(),
      ownerEmail: values.ownerEmail.trim(),
      requestId: createRequestId(),
    };
    inFlight.current = true;
    setReceipt(null);
    setFeedback(null);
    mutation.mutate({
      command,
      generation: operationGeneration.current,
      userId,
    });
  };

  const retry = () => {
    if (
      !attemptRef.current ||
      inFlight.current ||
      !isCurrentOperation(
        attemptRef.current,
        operationGeneration,
        currentUserIdRef,
      )
    ) {
      return;
    }
    inFlight.current = true;
    setFeedback(null);
    mutation.mutate(attemptRef.current);
  };

  if (!bootstrap) {
    return <CircularProgress aria-label="Carregando administração" />;
  }

  if (!isOperator) {
    return (
      <Stack spacing={3}>
        <PageHeader
          description="Provisionamento de organizações para operadores de plataforma."
          eyebrow="Administração"
          title="Administração da plataforma"
        />
        <Alert severity="info">
          Esta área está disponível apenas para operadores de plataforma.
        </Alert>
      </Stack>
    );
  }

  const inputsLocked = mfaBlocked || Boolean(attempt) || mutation.isPending;

  return (
    <Stack spacing={3}>
      <PageHeader
        description="Crie a organização inicial com grupo, empresa e dono; o convite do dono fica pendente de entrega."
        eyebrow="Administração"
        title="Administração da plataforma"
      />
      {mfaBlocked ? (
        <Alert severity="warning">
          <Stack spacing={2}>
            Esta ação exige autenticação com segundo fator. Saia e entre
            novamente com MFA para continuar.
            <Button onClick={() => void signOut()} variant="outlined">
              Sair
            </Button>
          </Stack>
        </Alert>
      ) : null}
      {receipt ? (
        <Alert severity="success">{receiptMessage(receipt)}</Alert>
      ) : null}
      {attempt && !mutation.isPending ? (
        <Alert severity="warning">
          A tentativa anterior terminou sem resposta confirmada. Tente novamente
          com os mesmos dados antes de criar outra organização.
        </Alert>
      ) : null}
      {feedback === "conflict" ? (
        <Alert severity="error">
          Não foi possível concluir: já existe um registro conflitante.
        </Alert>
      ) : null}
      {feedback === "rate_limited" ? (
        <Alert severity="error">
          Muitas tentativas. Aguarde antes de tentar novamente.
        </Alert>
      ) : null}
      {feedback === "session" ? (
        <Alert severity="error">
          Sua sessão não permite mais esta ação. Entre novamente.
        </Alert>
      ) : null}
      {feedback === "error" ? (
        <Alert severity="error">Não foi possível criar a organização.</Alert>
      ) : null}
      <Card component="section" variant="outlined">
        <CardContent>
          <Stack
            aria-busy={mutation.isPending}
            component="form"
            noValidate
            onSubmit={form.handleSubmit(reserve)}
            spacing={2}
          >
            <TextField
              {...form.register("groupName")}
              disabled={inputsLocked}
              error={Boolean(form.formState.errors.groupName)}
              helperText={form.formState.errors.groupName?.message}
              label="Nome do grupo"
            />
            <TextField
              {...form.register("companyName")}
              disabled={inputsLocked}
              error={Boolean(form.formState.errors.companyName)}
              helperText={form.formState.errors.companyName?.message}
              label="Nome da empresa"
            />
            <TextField
              {...form.register("ownerDisplayName")}
              disabled={inputsLocked}
              error={Boolean(form.formState.errors.ownerDisplayName)}
              helperText={form.formState.errors.ownerDisplayName?.message}
              label="Nome do dono"
            />
            <TextField
              {...form.register("ownerEmail")}
              disabled={inputsLocked}
              error={Boolean(form.formState.errors.ownerEmail)}
              helperText={form.formState.errors.ownerEmail?.message}
              label="E-mail do dono"
              type="email"
            />
            <Button
              disabled={inputsLocked || mutation.isPending}
              type="submit"
              variant="contained"
            >
              Criar organização
            </Button>
            {mutation.isPending ? (
              <Stack
                direction="row"
                role="status"
                spacing={1}
                sx={{ alignItems: "center" }}
              >
                <CircularProgress aria-label="Criando organização" size={20} />
                Criando organização…
              </Stack>
            ) : null}
            {attempt && !mutation.isPending ? (
              <Button onClick={retry} variant="outlined">
                Tentar novamente
              </Button>
            ) : null}
          </Stack>
        </CardContent>
      </Card>
    </Stack>
  );
}

function receiptMessage(receipt: ProvisionReceipt): string {
  const creationMessage =
    receipt.state === "created"
      ? "Organização criada."
      : "Organização já havia sido criada.";
  return `${creationMessage} ${invitationStatusMessage[receipt.invitationStatus]}`;
}

function feedbackFor(failure: PlatformOrganizationFailure): ProvisionFeedback {
  if (
    failure.kind === "idempotency_conflict" ||
    failure.kind === "organization_conflict"
  ) {
    return "conflict";
  }
  if (failure.kind === "rate_limited") {
    return "rate_limited";
  }
  return "error";
}

function isAuthorizationFailure(failure: PlatformOrganizationFailure): boolean {
  return (
    failure.kind === "unauthorized" ||
    failure.kind === "platform_operator_required"
  );
}

function isAmbiguous(failure: PlatformOrganizationFailure): boolean {
  // Somente falhas sinalizadas com mayHaveCommitted podem ter chegado ao
  // servidor; falhas de antiforgery ou de validação local são definitivas.
  return failure.mayHaveCommitted === true;
}

function isCurrentOperation(
  operation: ProvisionOperation,
  operationGeneration: MutableRefObject<number>,
  userId: MutableRefObject<string | null>,
): boolean {
  return (
    operation.generation === operationGeneration.current &&
    operation.userId === userId.current
  );
}
