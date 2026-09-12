# Contract Boundary

These folders reserve implementation homes for the accepted D03 taxonomy. D01
does not select transport, broker, serialization, endpoint, or programming
language and therefore does not create speculative contract schemas.

Cross-service behavior must use published contracts. A consumer must never
import another service's domain or persistence implementation merely to reuse a
type.
