from dataclasses import dataclass
from typing import List, Optional
import logging

logger = logging.getLogger(__name__)


@dataclass
class User:
    id: int
    name: str
    email: str


class UserRepository:
    def __init__(self, db_connection):
        self.db = db_connection

    def get_by_id(self, user_id: int) -> Optional[User]:
        logger.info(f"Getting user {user_id}")
        return self.db.query(User).get(user_id)

    def list_all(self) -> List[User]:
        return self.db.query(User).all()

    def create(self, user: User) -> User:
        self.db.add(user)
        self.db.commit()
        return user


def format_user_name(user: User) -> str:
    return f"{user.name} ({user.email})"


class UserService:
    def __init__(self, repo: UserRepository):
        self.repo = repo

    def get_user(self, user_id: int) -> Optional[User]:
        return self.repo.get_by_id(user_id)

    def create_user(self, name: str, email: str) -> User:
        user = User(id=0, name=name, email=email)
        return self.repo.create(user)
