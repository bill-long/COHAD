import { Observable, Subject, BehaviorSubject } from 'rxjs';
import { InjectionToken } from '@angular/core';
import { scan } from 'rxjs/operators';
import { environment } from 'src/environments/environment';
import { ApiUser, AuthUser, Home, DirectoryHome } from './models';

export interface ApplicationState {
    allHomes: Home[],
    allUsers: ApiUser[],
    authUser: AuthUser | null,
    apiUser: ApiUser | null,
    directory: DirectoryHome[],
    operationsInProgress: number,
    authBootstrapStatus: 'idle' | 'inProgress' | 'completed',
    /** True after OAuth tryLogin / mock-auth has finished establishing session (or lack thereof). Used so guards do not act on the initial null user before bootstrap. */
    authSessionResolved: boolean
}

export const initialStateValue: ApplicationState = {
    allHomes: [],
    allUsers: [],
    authUser: null,
    apiUser: null,
    directory: [],
    operationsInProgress: 0,
    authBootstrapStatus: 'idle',
    authSessionResolved: false
}

export class AuthenticatedUserChanged { constructor(public authUser: AuthUser | null) { } }

export class LoadAllHomes { }

export class LoadAllHomesCompleted { constructor(public homes: Home[]) { } }

export class LoadAllUsers { }

export class LoadAllUsersCompleted { constructor(public users: ApiUser[]) { } }

export class LoadDirectory { }

export class LoadDirectoryCompleted { constructor(public data: DirectoryHome[]) { } }

export class LoadUser { }

export class LoadUserCompleted { constructor(public user: ApiUser | null) { } }

export class Login { constructor(public redirectTo?: string) { } }

export class MockLogin { constructor(public userId: string, public redirectTo?: string) { } }

export class Logout { }

export class AuthSessionResolved { }

export type Action =
    AuthenticatedUserChanged |
    LoadAllHomes |
    LoadAllHomesCompleted |
    LoadAllUsers |
    LoadAllUsersCompleted |
    LoadDirectory |
    LoadDirectoryCompleted |
    LoadUser |
    LoadUserCompleted |
    Login |
    MockLogin |
    Logout |
    AuthSessionResolved;

export const dispatcher = new InjectionToken<Subject<Action>>('dispatcher');

export const initialState = new InjectionToken<ApplicationState>('initialState');

export const applicationState = new InjectionToken<ApplicationState>('applicationState');

export function applicationStateFactory(initialState: ApplicationState, dispatcher: Observable<Action>): Observable<ApplicationState> {

    let appStateObservable = dispatcher.pipe(
        scan((state: ApplicationState, action: Action) => {

            if (!environment.production) {
                console.log('Processing action ', action);
            }

            let newState: ApplicationState;

            if (action instanceof AuthenticatedUserChanged) {
                const hasAccessToken = action.authUser?.accessToken != null;
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: action.authUser,
                    // Always clear apiUser when auth identity changes to prevent stale role/navigation state.
                    apiUser: null,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress,
                    authBootstrapStatus: hasAccessToken ? 'inProgress' : 'idle',
                    authSessionResolved: state.authSessionResolved
                };
            } else if (action instanceof AuthSessionResolved) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress,
                    authBootstrapStatus: state.authBootstrapStatus,
                    authSessionResolved: true
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
                    operationsInProgress: state.operationsInProgress - 1,
                    authBootstrapStatus: state.authBootstrapStatus,
                    authSessionResolved: state.authSessionResolved
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
                    operationsInProgress: state.operationsInProgress - 1,
                    authBootstrapStatus: state.authBootstrapStatus,
                    authSessionResolved: state.authSessionResolved
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
                    operationsInProgress: state.operationsInProgress - 1,
                    authBootstrapStatus: state.authBootstrapStatus,
                    authSessionResolved: state.authSessionResolved
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
                    operationsInProgress: state.operationsInProgress - 1,
                    authBootstrapStatus: state.authUser?.accessToken != null ? 'completed' : 'idle',
                    authSessionResolved: state.authSessionResolved
                };
            } else if (action instanceof Login) {
                newState = state;
            } else if (action instanceof MockLogin) {
                newState = state;
            } else if (action instanceof Logout) {
                newState = state;
            } else {
                newState = state;
            }

            if (!environment.production) {
                console.log('Emitting new state', newState);
            }

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
        operationsInProgress: state.operationsInProgress + 1,
        authBootstrapStatus: state.authBootstrapStatus,
        authSessionResolved: state.authSessionResolved
    };
}
