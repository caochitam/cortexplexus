import { Injectable } from '@angular/core';
import { HttpClient } from './http-client';

export interface IUserService {
  getUser(id: number): Promise<User>;
  listUsers(): Promise<User[]>;
}

export class UserService implements IUserService {
  private http: HttpClient;

  constructor(http: HttpClient) {
    this.http = http;
  }

  async getUser(id: number): Promise<User> {
    return this.http.get(`/api/users/${id}`);
  }

  async listUsers(): Promise<User[]> {
    return this.http.get('/api/users');
  }
}

export function formatUserName(user: User): string {
  return `${user.firstName} ${user.lastName}`;
}

export enum UserRole {
  Admin = 'admin',
  User = 'user',
  Guest = 'guest',
}

export type UserId = string | number;
