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
    operationsInProgress: number,
    printDirectorySettings: {
        frontCoverDataUrl: string | null,
        mapLeftDataUrl: string | null,
        mapRightDataUrl: string | null,
        backCoverDataUrl: string | null,
        addExtraPageBreak: boolean
    }
}

export const initialStateValue: ApplicationState = {
    allHomes: [],
    allUsers: [],
    authUser: null,
    apiUser: null,
    directory: [],
    operationsInProgress: 0,
    printDirectorySettings: {
      frontCoverDataUrl: null,
      mapLeftDataUrl: null,
      mapRightDataUrl: null,
      backCoverDataUrl: null,
      addExtraPageBreak: false
    }
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

export class SetPrintDirectoryFrontCover { constructor(public frontCoverDataUrl: string | null) { } }

export class SetPrintDirectoryLeftMap { constructor(public mapLeftDataUrl: string | null) { } }

export class SetPrintDirectoryRightMap { constructor(public mapRightDataUrl: string | null) { } }

export class SetPrintDirectoryBackCover { constructor(public backCoverDataUrl: string | null) { } }

export class SetPrintDirectoryAddExtraPageBreak { constructor(public addExtraPageBreak: boolean) { } }

export type Action =
    AuthenticatedUserChanged |
    LoadDirectory |
    LoadDirectoryCompleted |
    LoadUser |
    LoadUserCompleted |
    Login |
    Logout |
    SetPrintDirectoryFrontCover |
    SetPrintDirectoryLeftMap |
    SetPrintDirectoryRightMap |
    SetPrintDirectoryBackCover |
    SetPrintDirectoryAddExtraPageBreak;

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
                    operationsInProgress: state.operationsInProgress,
                    printDirectorySettings: state.printDirectorySettings
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
                    printDirectorySettings: state.printDirectorySettings
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
                    printDirectorySettings: state.printDirectorySettings
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
                    printDirectorySettings: state.printDirectorySettings
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
                    printDirectorySettings: state.printDirectorySettings
                };
            } else if (action instanceof Login) {
                newState = state;
            } else if (action instanceof Logout) {
                newState = state;
            } else if (action instanceof SetPrintDirectoryFrontCover) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress,
                    printDirectorySettings: {
                        frontCoverDataUrl: action.frontCoverDataUrl,
                        mapLeftDataUrl: state.printDirectorySettings.mapLeftDataUrl,
                        mapRightDataUrl: state.printDirectorySettings.mapRightDataUrl,
                        backCoverDataUrl: state.printDirectorySettings.backCoverDataUrl,
                        addExtraPageBreak: state.printDirectorySettings.addExtraPageBreak
                    }
                };
            } else if (action instanceof SetPrintDirectoryLeftMap) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress,
                    printDirectorySettings: {
                        frontCoverDataUrl: state.printDirectorySettings.frontCoverDataUrl,
                        mapLeftDataUrl: action.mapLeftDataUrl,
                        mapRightDataUrl: state.printDirectorySettings.mapRightDataUrl,
                        backCoverDataUrl: state.printDirectorySettings.backCoverDataUrl,
                        addExtraPageBreak: state.printDirectorySettings.addExtraPageBreak
                    }
                };
            } else if (action instanceof SetPrintDirectoryRightMap) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress,
                    printDirectorySettings: {
                        frontCoverDataUrl: state.printDirectorySettings.frontCoverDataUrl,
                        mapLeftDataUrl: state.printDirectorySettings.mapLeftDataUrl,
                        mapRightDataUrl: action.mapRightDataUrl,
                        backCoverDataUrl: state.printDirectorySettings.backCoverDataUrl,
                        addExtraPageBreak: state.printDirectorySettings.addExtraPageBreak
                    }
                };
            } else if (action instanceof SetPrintDirectoryBackCover) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress,
                    printDirectorySettings: {
                        frontCoverDataUrl: state.printDirectorySettings.frontCoverDataUrl,
                        mapLeftDataUrl: state.printDirectorySettings.mapLeftDataUrl,
                        mapRightDataUrl: state.printDirectorySettings.mapRightDataUrl,
                        backCoverDataUrl: action.backCoverDataUrl,
                        addExtraPageBreak: state.printDirectorySettings.addExtraPageBreak
                    }
                };
            } else if (action instanceof SetPrintDirectoryAddExtraPageBreak) {
                newState = {
                    allHomes: state.allHomes,
                    allUsers: state.allUsers,
                    authUser: state.authUser,
                    apiUser: state.apiUser,
                    directory: state.directory,
                    operationsInProgress: state.operationsInProgress,
                    printDirectorySettings: {
                        frontCoverDataUrl: state.printDirectorySettings.frontCoverDataUrl,
                        mapLeftDataUrl: state.printDirectorySettings.mapLeftDataUrl,
                        mapRightDataUrl: state.printDirectorySettings.mapRightDataUrl,
                        backCoverDataUrl: state.printDirectorySettings.backCoverDataUrl,
                        addExtraPageBreak: action.addExtraPageBreak
                    }
                };
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
        operationsInProgress: state.operationsInProgress + 1,
        printDirectorySettings: state.printDirectorySettings
    };
}
