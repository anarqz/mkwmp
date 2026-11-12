import unittest

from local_matchmaking import LocalMatchmaking


class LocalMatchmakingTest(unittest.TestCase):
    def test_two_distinct_users_reserve_and_join_the_same_lobby(self):
        match = LocalMatchmaking()
        first = match.create("one", "race")
        self.assertIs(match.create("one", "race"), first)
        self.assertIsNone(first.lobby)
        self.assertIsNone(match.create("three", "battle").lobby)
        second = match.create("two", "race")
        self.assertIsNotNone(first.lobby)
        self.assertEqual(first.lobby, second.lobby)
        with self.assertRaises(ValueError):
            match.join("two", first.id, first.lobby)
        lobby = match.join("one", first.id, first.lobby)
        match.join("two", second.id, second.lobby)
        self.assertEqual(lobby.joined, {first.id, second.id})

    def test_cancel_before_both_players_join_requeues_the_other_seat(self):
        match = LocalMatchmaking()
        first = match.create("one", "race")
        second = match.create("two", "race")
        old_lobby = first.lobby
        match.join("one", first.id, old_lobby)
        self.assertFalse(match.cancel("one", second.id))
        self.assertTrue(match.cancel("two", second.id))
        self.assertIsNone(first.lobby)
        replacement = match.create("three", "race")
        self.assertEqual(first.lobby, replacement.lobby)
        self.assertNotEqual(first.lobby, old_lobby)


if __name__ == "__main__":
    unittest.main()
