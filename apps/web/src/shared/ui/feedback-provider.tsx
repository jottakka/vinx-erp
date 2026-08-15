import { Alert, Snackbar } from "@mui/material";
import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
} from "react";

type FeedbackSeverity = "success" | "info" | "warning" | "error";

type FeedbackMessage = {
  message: string;
  severity: FeedbackSeverity;
};

type FeedbackContextValue = {
  notify: (message: string, severity?: FeedbackSeverity) => void;
};

const FeedbackContext = createContext<FeedbackContextValue | null>(null);

const autoHideDuration = 6000;

export function FeedbackProvider({ children }: { children: ReactNode }) {
  const [feedback, setFeedback] = useState<FeedbackMessage | null>(null);
  const open = feedback !== null;
  const sequence = useRef(0);

  const notify = useCallback(
    (message: string, severity: FeedbackSeverity = "success") => {
      sequence.current += 1;
      setFeedback({ message, severity });
    },
    [],
  );

  const value = useMemo(() => ({ notify }), [notify]);

  return (
    <FeedbackContext.Provider value={value}>
      {children}
      <Snackbar
        autoHideDuration={autoHideDuration}
        key={feedback ? sequence.current : 0}
        onClose={(_, reason) => {
          if (reason === "clickaway") {
            return;
          }

          setFeedback(null);
        }}
        open={open}
      >
        {feedback ? (
          <Alert
            onClose={() => setFeedback(null)}
            role="status"
            severity={feedback.severity}
            variant="filled"
          >
            {feedback.message}
          </Alert>
        ) : undefined}
      </Snackbar>
    </FeedbackContext.Provider>
  );
}

export function useFeedback(): FeedbackContextValue {
  const feedback = useContext(FeedbackContext);

  if (!feedback) {
    throw new Error("useFeedback deve ser usado dentro de FeedbackProvider");
  }

  return feedback;
}
