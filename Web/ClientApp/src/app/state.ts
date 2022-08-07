import { Observable, Subject, BehaviorSubject } from 'rxjs';
import { InjectionToken } from '@angular/core';
import { scan } from 'rxjs/operators';
import { ApiUser, AuthUser, Home, DirectoryHome } from './models';

export interface ApplicationState {
    allHomes: Home[],
    allUsers: ApiUser[],
    authUser: AuthUser | null,
    apiUser: ApiUser | null,
    directory: DirectoryHome[],
    operationsInProgress: number
}

export const initialStateValue: ApplicationState = {
    allHomes: [],
    allUsers: [],
    authUser: null,
    apiUser: null,
    directory: [],
    operationsInProgress: 0
}

export class AuthenticatedUserChanged { constructor(public authUser: AuthUser) { } }

export class LoadAllHomes { }

export class LoadAllHomesCompleted { constructor(public homes: Home[]) { } }

export class LoadAllUsers { }

export class LoadAllUsersCompleted { constructor(public users: ApiUser[]) { } }

export class LoadDirectory { }

export class LoadDirectoryCompleted { constructor(public data: DirectoryHome[]) { } }

export class LoadUser { }

export class LoadUserCompleted { constructor(public user: ApiUser | null) { } }

export class Login { }

export class Logout { }

export type Action =
    AuthenticatedUserChanged |
    LoadDirectory |
    LoadDirectoryCompleted |
    LoadUser |
    LoadUserCompleted |
    Login |
    Logout;

export const dispatcher = new InjectionToken<Subject<Action>>('dispatcher');

export const initialState = new InjectionToken<ApplicationState>('initialState');

export const applicationState = new InjectionToken<ApplicationState>('applicationState');

export function applicationStateFactory(initialState: ApplicationState, dispatcher: Observable<Action>): Observable<ApplicationState> {

    let appStateObservable = dispatcher.pipe(
        scan((state: ApplicationState, action: Action) => {

            console.log('Processing action ', action);

            let newState: ApplicationState;

            if (action instanceof AuthenticatedUserChanged) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: action.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress
                };
            } else if (action instanceof LoadAllHomes) {
                newState = addOperationInProgress(state);
            } else if (action instanceof LoadAllHomesCompleted) {
                newState = {
                    allHomes: action.homes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress - 1
                };
            } else if (action instanceof LoadAllUsers) {
                newState = addOperationInProgress(state);
            } else if (action instanceof LoadAllUsersCompleted) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: action.users,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress - 1
                };
            } else if (action instanceof LoadDirectory) {
                newState = addOperationInProgress(state);
            } else if (action instanceof LoadDirectoryCompleted) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: action.data,
                    operationsInProgress: state.operationsInProgress - 1
                };
            } else if (action instanceof LoadUser) {
                newState = addOperationInProgress(state);
            } else if (action instanceof LoadUserCompleted) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: action.user,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress - 1
                };
            } else if (action instanceof Login) {
                newState = state;
            } else if (action instanceof Logout) {
                newState = state;
            } else {
                newState = state;
            }

            console.log('Emitting new state', newState);

            return newState;
        }, initialState));

    const behaviorSubject = new BehaviorSubject<ApplicationState>(initialState);
    appStateObservable.subscribe(s => behaviorSubject.next(s));
    return behaviorSubject;
}

function addOperationInProgress(state: ApplicationState) {
    return {
        allHomes: state.allHomes,
        allUsers: state.allUsers,
        authUser: state.authUser,
        apiUser: state.apiUser,
        directory: state.directory,
        operationsInProgress: state.operationsInProgress + 1
    };
}
