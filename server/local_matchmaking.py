"""In-memory seats for the first two-client loopback match.

The NPLN adapter will translate game tickets and session messages into these
operations once their wire layouts are known.
"""

from dataclasses import dataclass, field
from uuid import UUID, uuid4


@dataclass
class Ticket:
    user: str
    mode: str
    id: UUID = field(default_factory=uuid4)
    lobby: UUID | None = None
    # Serialized Player message from the client's CreateMatchmakingTicket
    # (ticket field 4); echoed back in match assignments.
    player: bytes = b""


@dataclass
class Lobby:
    seats: tuple[UUID, UUID]
    id: UUID = field(default_factory=uuid4)
    joined: set[UUID] = field(default_factory=set)


class LocalMatchmaking:
    def __init__(self):
        self.tickets: dict[UUID, Ticket] = {}
        self.lobbies: dict[UUID, Lobby] = {}

    def create(self, user: str, mode: str, player: bytes = b"") -> Ticket:
        for ticket in self.tickets.values():
            if ticket.user == user:
                if ticket.mode != mode:
                    raise ValueError("Cancel the active ticket before changing mode.")
                return ticket
        ticket = Ticket(user, mode, player=player)
        self.tickets[ticket.id] = ticket
        self._pair(mode)
        return ticket

    def _pair(self, mode: str) -> None:
        queue = [t for t in self.tickets.values() if t.mode == mode and t.lobby is None]
        for first, second in zip(queue[::2], queue[1::2]):
            lobby = Lobby((first.id, second.id))
            self.lobbies[lobby.id] = lobby
            first.lobby = second.lobby = lobby.id

    def join(self, user: str, ticket_id: UUID, lobby_id: UUID) -> Lobby:
        ticket = self.tickets.get(ticket_id)
        if ticket is None or ticket.user != user or ticket.lobby != lobby_id:
            raise ValueError("This player has no seat in this lobby.")
        lobby = self.lobbies[lobby_id]
        lobby.joined.add(ticket_id)
        return lobby

    def cancel(self, user: str, ticket_id: UUID) -> bool:
        ticket = self.tickets.get(ticket_id)
        if ticket is None or ticket.user != user:
            return False
        del self.tickets[ticket_id]
        if ticket.lobby is not None:
            lobby = self.lobbies.pop(ticket.lobby)
            for other_id in lobby.seats:
                if other_id in self.tickets:
                    if len(lobby.joined) == len(lobby.seats):
                        # Close the started lobby. The other player can request
                        # a fresh ticket explicitly.
                        del self.tickets[other_id]
                    else:
                        self.tickets[other_id].lobby = None
            self._pair(ticket.mode)
        return True
